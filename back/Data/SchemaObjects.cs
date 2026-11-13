using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace ERental.Data;

/// <summary>
/// 数据库对象（视图 / 函数 / 触发器 / 存储过程）安装器。
/// 定义只有一份：database/objects.sql 以嵌入资源随程序集发布。
/// 启动时按内容哈希 + 对象存在性判断是否需要重装，避免每次启动都执行一遍 DDL。
/// <para>
/// 连接与锁：安装全程使用 <see cref="DbConnectionFactory"/> 建立的独立非池化连接。
/// GET_LOCK 是连接级的命名锁，DDL 又会隐式提交——把它放在 EF 的池化连接或调用方事务里，
/// 都会出现「锁看似释放、实际仍被池中连接持有」或「安装顺手提交了业务事务」的问题。
/// 独立连接把锁、会话时区与 DDL 的生命周期收在同一个方法内，方法结束即物理断开。
/// </para>
/// </summary>
internal static class SchemaObjects
{
    /// <summary>嵌入资源名（见 back.csproj 的 LogicalName）</summary>
    private const string ResourceName = "ERental.database.objects.sql";

    /// <summary>内部哈希在 system_config 中的键：SystemConfigService 会过滤 schema. 前缀</summary>
    private const string HashKey = "schema.objects_hash";

    /// <summary>安装时使用的 MySQL 命名锁，避免多实例同时启动时重复执行 DDL</summary>
    private const string LockName = "erental:schema_objects";

    /// <summary>等待命名锁的秒数</summary>
    private const int LockTimeoutSeconds = 30;

    private static readonly string[] ExpectedViews =
    [
        "v_order_overview",
        "v_settlement_bill",
        "v_vehicle_maintenance_due"
    ];

    private static readonly string[] ExpectedFunctions =
    [
        "fn_effective_daily_rent",
        "fn_member_discount",
        "fn_billable_hours"
    ];

    private static readonly string[] ExpectedTriggers =
    [
        "trg_order_status_transition",
        "trg_order_terminal_release",
        "trg_renewal_sync_lease",
        "trg_payment_paid_insert",
        "trg_payment_paid_update",
        "trg_message_receiver_insert",
        "trg_message_receiver_update"
    ];

    private static readonly string[] ExpectedProcedures =
    [
        "sp_claim_vehicle",
        "sp_release_vehicle",
        "sp_cancel_order",
        "sp_cancel_pending_order",
        "sp_apply_renewal",
        "sp_cancel_expired_pending_orders"
    ];

    /// <summary>读取嵌入的 objects.sql 并确保数据库中的对象与文件一致</summary>
    public static void EnsureInstalled(AppDbContext db)
    {
        // 安装包含 DDL：MySQL 的 DDL 会隐式提交，挂在业务长事务里会把半截事务提交掉。
        // 这里显式拒绝，避免调用方在错误时机触发安装。
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException(
                "数据库对象安装不能在事务中执行：安装包含 DDL（MySQL 会隐式提交），" +
                "且 GET_LOCK 只对当前连接有效。请在启动阶段、业务事务开始之前执行安装。");

        var sql = LoadSql();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();

        using var connection = DbConnectionFactory.CreateDedicated(db);
        connection.Open();
        // 独立连接不经过 EF 拦截器，必须显式同步会话时区：
        // 安装器最后要写 NOW() 作为 update_time，不能让它落回服务端时区。
        MySqlSessionTimeZoneInterceptor.ApplySessionTimeZone(connection);

        if (ObjectsReady(connection, hash)) return;

        // GET_LOCK 是「连接级」的：拿锁、执行 DDL、释放锁必须落在同一条物理连接上。
        // DbConnectionFactory 已强制 Pooling=false，连接关闭即释放锁，不存在池化复用残留。
        if (!TryAcquireLock(connection))
        {
            Console.WriteLine("[数据库对象] 未取得安装锁，跳过本次安装（其他实例正在安装）");
            return;
        }

        try
        {
            // 拿到锁后重新判断：先到的实例可能已经装完
            if (ObjectsReady(connection, hash)) return;

            // 旧版本的视图映射同时带 [Table]，EnsureCreated 会建出同名空表，
            // 随后 CREATE OR REPLACE VIEW 会报 “is not VIEW”。先清掉这种占位空表。
            DropShadowViewTables(connection);

            var statements = SqlScript.Split(sql);
            var executed = 0;
            foreach (var statement in statements)
            {
                Execute(connection, statement);
                executed++;
            }

            SaveHash(connection, hash);
            Console.WriteLine($"[数据库对象] 已安装 {executed} 条语句（{ExpectedViews.Length} 个视图 / {ExpectedFunctions.Length} 个函数 / {ExpectedTriggers.Length} 个触发器 / {ExpectedProcedures.Length} 个存储过程）");
        }
        finally
        {
            // 释放失败也不影响安装结果：连接 dispose 时 MySQL 会回收该连接持有的全部命名锁
            ReleaseLock(connection);
        }
    }

    /// <summary>
    /// 清理旧版建表遗留的「视图同名空表」。
    /// 早期视图映射类同时标了 [Table("v_xxx")]，EnsureCreated() 会把它当成普通表建出来；
    /// 后来 CREATE OR REPLACE VIEW 撞上同名表就会失败。
    /// 只删除确认为空表的占位物；表里一旦有数据则拒绝自动操作，避免误删。
    /// </summary>
    private static void DropShadowViewTables(DbConnection connection)
    {
        foreach (var view in ExpectedViews)
        {
            if (!IsBaseTable(connection, view)) continue;

            var rows = Convert.ToInt64(Scalar(connection, $"SELECT COUNT(*) FROM `{view}`") ?? 0);
            if (rows > 0)
                throw new InvalidOperationException(
                    $"数据库中存在与视图同名的表 `{view}`，且含 {rows} 行数据。" +
                    "无法自动转换为视图，请先备份并迁移该表数据，再删除表后重启。");

            Execute(connection, $"DROP TABLE `{view}`");
            Console.WriteLine($"[数据库对象] 已清理旧版遗留的空占位表 `{view}`");
        }
    }

    /// <summary>判断当前库中是否存在同名 BASE TABLE（视图不算）</summary>
    private static bool IsBaseTable(DbConnection connection, string name)
    {
        var count = Scalar(connection,
            "SELECT COUNT(*) FROM information_schema.tables " +
            "WHERE table_schema = DATABASE() AND table_name = @n AND table_type = 'BASE TABLE'",
            ("@n", name));
        return Convert.ToInt32(count ?? 0) > 0;
    }

    /// <summary>哈希一致且所有对象都存在时无需重装</summary>
    private static bool ObjectsReady(DbConnection connection, string hash)
    {
        var stored = ScalarString(connection,
            "SELECT `config_value` FROM `system_config` WHERE `config_key` = @k",
            ("@k", HashKey));
        if (!string.Equals(stored, hash, StringComparison.OrdinalIgnoreCase)) return false;

        return CountExisting(connection, "VIEWS", "TABLE_SCHEMA", "TABLE_NAME", ExpectedViews) == ExpectedViews.Length
            && CountExisting(connection, "TRIGGERS", "TRIGGER_SCHEMA", "TRIGGER_NAME", ExpectedTriggers) == ExpectedTriggers.Length
            && CountRoutines(connection, "PROCEDURE", ExpectedProcedures) == ExpectedProcedures.Length
            && CountRoutines(connection, "FUNCTION", ExpectedFunctions) == ExpectedFunctions.Length;
    }

    /// <summary>统计 information_schema 中已存在的对象个数</summary>
    private static int CountExisting(DbConnection connection, string table, string schemaColumn, string nameColumn, string[] names)
    {
        var placeholders = string.Join(",", names.Select((_, i) => $"@p{i}"));
        var sql = $"SELECT COUNT(*) FROM information_schema.`{table}` WHERE `{schemaColumn}` = DATABASE() AND `{nameColumn}` IN ({placeholders})";
        var parameters = names.Select((name, i) => ($"@p{i}", (object)name)).ToArray();
        return Convert.ToInt32(Scalar(connection, sql, parameters) ?? 0);
    }

    /// <summary>按类型统计存储过程/函数：ROUTINES 表同时包含二者，必须分开数</summary>
    private static int CountRoutines(DbConnection connection, string routineType, string[] names)
    {
        var placeholders = string.Join(",", names.Select((_, i) => $"@p{i}"));
        var sql = $"SELECT COUNT(*) FROM information_schema.`ROUTINES` " +
            "WHERE `ROUTINE_SCHEMA` = DATABASE() AND `ROUTINE_TYPE` = @routineType " +
            $"AND `ROUTINE_NAME` IN ({placeholders})";
        var parameters = new List<(string, object)> { ("@routineType", routineType) };
        parameters.AddRange(names.Select((name, i) => ($"@p{i}", (object)name)));
        return Convert.ToInt32(Scalar(connection, sql, parameters.ToArray()) ?? 0);
    }

    /// <summary>写入安装哈希（配置键唯一，重复安装时覆盖值）</summary>
    private static void SaveHash(DbConnection connection, string hash)
    {
        // update_time 必须显式给值：空库由 EnsureCreated 建表，列上没有数据库默认值，
        // 依赖 DEFAULT 会在安装最后一步报 “Field 'update_time' doesn't have a default value”。
        Execute(connection,
            "INSERT INTO `system_config` (`config_key`, `config_value`, `config_desc`, `update_time`) VALUES (@k, @v, @d, NOW()) " +
            "ON DUPLICATE KEY UPDATE `config_value` = VALUES(`config_value`), `update_time` = NOW()",
            ("@k", HashKey), ("@v", hash), ("@d", "数据库对象安装哈希（内部使用，请勿修改）"));
    }

    private static bool TryAcquireLock(DbConnection connection)
    {
        var value = Scalar(connection, "SELECT GET_LOCK(@name, @timeout)",
            ("@name", LockName), ("@timeout", LockTimeoutSeconds));
        return value is not null and not DBNull && Convert.ToInt32(value) == 1;
    }

    private static void ReleaseLock(DbConnection connection)
    {
        try
        {
            var released = Scalar(connection, "SELECT RELEASE_LOCK(@name)", ("@name", LockName));
            if (released is null or DBNull || Convert.ToInt32(released) == 0)
                Console.WriteLine("[数据库对象] 释放安装锁时发现锁已不属于当前连接（可能已被 MySQL 回收）");
        }
        catch (Exception ex)
        {
            // 释放失败不影响本次安装结果；连接关闭时 MySQL 也会自动释放命名锁
            Console.WriteLine($"[数据库对象] 释放安装锁失败：{ex.Message}");
        }
    }

    private static void Execute(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = CreateCommand(connection, sql, parameters);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = CreateCommand(connection, sql, parameters);
        return cmd.ExecuteScalar();
    }

    private static string? ScalarString(DbConnection connection, string sql, (string Name, object Value) arg)
    {
        var value = Scalar(connection, sql, arg);
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static DbCommand CreateCommand(DbConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) AddParameter(cmd, name, value);
        return cmd;
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static string LoadSql()
    {
        using var stream = typeof(SchemaObjects).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"找不到嵌入资源 {ResourceName}，请确认 back.csproj 已包含 database/objects.sql");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// 把 mysql 客户端风格的脚本拆成可逐条执行的语句：
/// objects.sql 用 DELIMITER $$ 包裹触发器/存储过程，驱动层不需要 DELIMITER，
/// 因此这里把 DELIMITER 指令本身丢掉，只按它决定语句的收尾标志。
/// </summary>
internal static class SqlScript
{
    /// <summary>按 DELIMITER 规则拆分 SQL 脚本</summary>
    public static List<string> Split(string script)
    {
        var statements = new List<string>();
        var buffer = new StringBuilder();
        var delimiter = ";";

        foreach (var rawLine in script.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("DELIMITER ", StringComparison.OrdinalIgnoreCase))
            {
                delimiter = trimmed["DELIMITER ".Length..].Trim();
                continue;
            }

            // 语句未结束时按原样累积；结束标志可能在同一行（如 END$$）
            if (line.TrimEnd().EndsWith(delimiter, StringComparison.Ordinal))
            {
                var cut = line.TrimEnd();
                buffer.AppendLine(cut[..^delimiter.Length]);
                var sql = buffer.ToString().Trim();
                if (HasExecutable(sql)) statements.Add(sql);
                buffer.Clear();
            }
            else
            {
                buffer.AppendLine(line);
            }
        }

        var tail = buffer.ToString().Trim();
        if (HasExecutable(tail)) statements.Add(tail);
        return statements;
    }

    /// <summary>排除只有注释/空白的片段（文件头说明、分节标题等）</summary>
    private static bool HasExecutable(string sql) =>
        sql.Split('\n').Any(line =>
        {
            var t = line.Trim();
            return t.Length > 0 && !t.StartsWith("--", StringComparison.Ordinal) && !t.StartsWith('#');
        });
}

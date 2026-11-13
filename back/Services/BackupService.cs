using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERental.Data;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

///<summary>备份文件摘要（供管理员端列表展示）</summary>
public sealed record BackupFileInfo(string FileName, long SizeBytes, DateTime CreatedAt, bool Encrypted);

///<summary>备份结果</summary>
public sealed record BackupResult(string FileName, long SizeBytes, int TableCount, bool Encrypted, long ElapsedMs);

///<summary>恢复结果</summary>
public sealed record RestoreResult(string FileName, int TableCount, long Rows, string SafetyBackup, long ElapsedMs);

///<summary>数据库备份/恢复</summary>
public class BackupService(AppDbContext db, IConfiguration config, ILogger<BackupService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly IConfiguration _cfg = config;
    private readonly ILogger<BackupService> _logger = logger;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ERBK1");
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string FilePrefix = "backup_";
    private const string SafetyPrefix = "auto-before-restore_";

    ///<summary>17 张表，父表在前</summary>
    private static readonly string[] Tables =
    [
        "role","permission","system_user","customer","member_rights","vehicle",
        "rental_order","payment_record","settle_record","maintenance_order","scrap_record",
        "system_config","system_log","renewal_record","car_check_list","check_report",
        "message"
    ];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    ///<summary>备份目录：默认程序目录下 backups/，可用 Backup:Directory 改到独立磁盘</summary>
    public string Directory
    {
        get
        {
            var configured = _cfg["Backup:Directory"];
            var dir = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(AppContext.BaseDirectory, "backups")
                : configured.Trim();
            return Path.GetFullPath(dir);
        }
    }

    ///<summary>保留份数（默认 14）</summary>
    private int KeepCount => int.TryParse(_cfg["Backup:Keep"], out var n) && n > 0 ? n : 14;


    ///<summary>生成一份加密备份ct 由后台定时备份传入 stoppingToken</summary>
    public async Task<BackupResult> CreateAsync(string prefix = FilePrefix, CancellationToken ct = default)
    {
        //计时口径=读库+序列化+加密+落盘+清理旧文件，也就是用户点下按钮后实际等待的时长
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tables = new Dictionary<string, List<Dictionary<string, object?>>>();
        long rows = 0;

        var conn = _db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await conn.OpenAsync(ct);
        try
        {
            foreach (var table in Tables)
            {
                var list = new List<Dictionary<string, object?>>();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT * FROM `{table}`";//NOSONAR `table` 取自类内常量 Tables，标识符不能参数化
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        //DBNull 一律转 null：JSON 里就是 null，恢复时再还原成 DBNull
                        row[reader.GetName(i)] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                    }
                    list.Add(row);
                }
                tables[table] = list;
                rows += list.Count;
            }
        }
        finally
        {
            if (!wasOpen) await conn.CloseAsync();
        }

        var payload = new
        {
            format = "erental-backup",
            version = 1,
            createdAt = DateTime.Now,
            tableCount = Tables.Length,
            rowCount = rows,
            tables
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        var encrypted = Encrypt(json);

        System.IO.Directory.CreateDirectory(Directory);
        var fileName = $"{prefix}{DateTime.Now:yyyyMMdd_HHmmss}.erbk";
        var path = Path.Combine(Directory, fileName);

        //先写临时文件再改名：中途断电/磁盘满不会留下一个"看起来存在但内容残缺"的备份
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, encrypted, ct);
        File.Move(tmp, path, overwrite: true);

        var size = new FileInfo(path).Length;
        ApplyRetention();

        sw.Stop();
        _logger.LogInformation("备份完成：{File}（{Size} KB，{Tables} 张表 / {Rows} 行，耗时 {Elapsed} ms）",
            fileName, size / 1024, Tables.Length, rows, sw.ElapsedMilliseconds);
        return new BackupResult(fileName, size, Tables.Length, true, sw.ElapsedMilliseconds);
    }

    ///<summary>备份文件列表（新的在前）</summary>
    public List<BackupFileInfo> ListFiles()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        return new DirectoryInfo(Directory)
            .GetFiles("*.erbk")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new BackupFileInfo(f.Name, f.Length, f.CreationTime, true))
            .ToList();
    }

    ///<summary>读取备份文件字节</summary>
    public async Task<(string FileName, byte[] Content)> ReadAsync(string fileName)
    {
        var path = ResolvePath(fileName);
        return (Path.GetFileName(path), await File.ReadAllBytesAsync(path));
    }


    ///<summary>用备份覆盖当前库</summary>
    public async Task<RestoreResult> RestoreAsync(string fileName)
    {
        //计时含「恢复前快照」：快照同样在用户等待期间发生，不计进去会好看但失真
        var sw = System.Diagnostics.Stopwatch.StartNew();

        //读文件/解密/解析收进一个方法：任何一步失败都在同一处抛出，主流程只剩「校验
        using var doc = await ReadBackupDocumentAsync(fileName);
        var tablesEl = ReadTablesNode(doc.RootElement);

        //恢复前快照：即使这份备份是错的，也能回到现在的状态
        var safety = await CreateAsync(SafetyPrefix);

        //列类型字典：按 information_schema 的真实类型转换 JSON 值
        var columnTypes = await LoadColumnTypesAsync();

        var rows = await RestoreTablesAsync(tablesEl, columnTypes);

        sw.Stop();
        _logger.LogInformation("数据恢复完成：{File}，{Tables} 张表 / {Rows} 行；恢复前快照 {Safety}；耗时 {Elapsed} ms",
            fileName, Tables.Length, rows, safety.FileName, sw.ElapsedMilliseconds);
        return new RestoreResult(fileName, Tables.Length, rows, safety.FileName, sw.ElapsedMilliseconds);
    }

    ///<summary>读取并解密备份文件，返回解析好的 JSON 文档（由调用方释放）</summary>
    private async Task<JsonDocument> ReadBackupDocumentAsync(string fileName)
    {
        var cipher = await File.ReadAllBytesAsync(ResolvePath(fileName));
        return JsonDocument.Parse(Decrypt(cipher));
    }

    ///<summary>校验备份结构：先要有 tables 对象，再核对格式标识（顺序与文案都沿用改造前）</summary>
    private static JsonElement ReadTablesNode(JsonElement root)
    {
        if (!root.TryGetProperty("tables", out var tablesEl) || tablesEl.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("备份文件结构不正确：缺少 tables 节点");
        if (root.TryGetProperty("format", out var fmt) && fmt.GetString() != "erental-backup")
            throw new InvalidOperationException("备份文件格式标识不匹配，拒绝恢复");
        return tablesEl;
    }

    ///<summary>清空并按备份重建各表，返回实际写入的行数</summary>
    private async Task<long> RestoreTablesAsync(JsonElement tablesEl, Dictionary<(string Table, string Column), string> columnTypes)
    {
        var conn = _db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await conn.OpenAsync();

        long rows = 0;
        var fkOff = false;
        try
        {
            await _db.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS=0");
            fkOff = true;

            //失败时不需要 catch：tx 在 using 结束时自动回滚
            await using var tx = await _db.Database.BeginTransactionAsync();
            await ClearTablesAsync();
            rows = await InsertTablesAsync(tablesEl, columnTypes);
            await tx.CommitAsync();
        }
        finally
        {
            await RestoreForeignKeyChecksAsync(fkOff);
            if (!wasOpen) await conn.CloseAsync();
        }
        return rows;
    }

    ///<summary>按建表顺序倒序清空</summary>
    private async Task ClearTablesAsync()
    {
        //表名不能用参数占位
        for (var i = Tables.Length - 1; i >= 0; i--)
        {
            var sql = string.Concat("DELETE FROM ", "\u0060", Tables[i], "\u0060");
            await _db.Database.ExecuteSqlRawAsync(sql);//NOSONAR 表名取自类内常量 Tables，是标识符不是可传参的值
        }
    }

    ///<summary>按父表在前的顺序逐行 INSERT，返回写入行数</summary>
    private async Task<long> InsertTablesAsync(JsonElement tablesEl, Dictionary<(string Table, string Column), string> columnTypes)
    {
        long rows = 0;
        foreach (var table in Tables)
        {
            if (!tablesEl.TryGetProperty(table, out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var rowEl in rowsEl.EnumerateArray())
            {
                var (sql, args) = BuildInsert(table, rowEl, columnTypes);
                await _db.Database.ExecuteSqlRawAsync(sql, args);
                rows++;
            }
        }
        return rows;
    }

    ///<summary>恢复外键检查开关：只在确实关过时才开回来，失败只记日志</summary>
    private async Task RestoreForeignKeyChecksAsync(bool fkOff)
    {
        if (!fkOff) return;
        try { await _db.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS=1"); }
        catch (Exception ex) { _logger.LogError(ex, "恢复外键检查失败，请重启后端进程"); }
    }

    ///<summary>构造单行 INSERT</summary>
    private static (string Sql, object[] Args) BuildInsert(
        string table, JsonElement rowEl, Dictionary<(string Table, string Column), string> columnTypes)
    {
        var cols = new List<string>();
        var placeholders = new List<string>();
        var args = new List<object>();

        foreach (var prop in rowEl.EnumerateObject())
        {
            if (!columnTypes.TryGetValue((table, prop.Name), out var dataType))
                throw new InvalidOperationException($"备份文件含未知列 {table}.{prop.Name}，已拒绝恢复");
            cols.Add($"`{prop.Name}`");

            //NULL 直接写字面量而不是传参数
            if (prop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                placeholders.Add("NULL");
                continue;
            }

            placeholders.Add($"@p{args.Count}");
            args.Add(ToDbValue(prop.Value, dataType)!);
        }

        if (cols.Count == 0)
            throw new InvalidOperationException($"备份文件中表 {table} 存在空行");

        return ($"INSERT INTO `{table}` ({string.Join(",", cols)}) VALUES ({string.Join(",", placeholders)})",
            args.ToArray());
    }

    private static object? ToDbValue(JsonElement v, string dataType)
    {
        if (v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return DBNull.Value;

        return dataType switch
        {
            "tinyint" or "smallint" or "mediumint" or "int" or "bigint" => v.GetInt64(),
            "decimal" or "numeric" => v.GetDecimal(),
            "float" or "double" or "real" => v.GetDouble(),
            "date" or "datetime" or "timestamp" =>
                v.ValueKind == JsonValueKind.String
                    ? DateTime.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.None)
                    : v.GetDateTime(),
            "blob" or "tinyblob" or "mediumblob" or "longblob" or "varbinary" or "binary" => v.GetBytesFromBase64(),
            _ => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
        };
    }

    private async Task<Dictionary<(string Table, string Column), string>> LoadColumnTypesAsync()
    {
        var map = new Dictionary<(string, string), string>();
        var conn = _db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                map[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2).ToLowerInvariant();
        }
        finally
        {
            if (!wasOpen) await conn.CloseAsync();
        }
        return map;
    }


    private byte[] ResolveKey()
    {
        var configured = _cfg["Backup:Key"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var key = Convert.FromBase64String(configured.Trim());
            if (key.Length != 32)
                throw new InvalidOperationException("Backup:Key 必须是 32 字节密钥的 Base64 编码（AES-256）");
            return key;
        }

        //未单独配置时从 JWT 密钥派生：不额外引入一个新的"必须保管的明文秘密"
        var jwt = _cfg["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(jwt))
            throw new InvalidOperationException("缺少 Jwt:Key，且未配置 Backup:Key，无法加密备份");
        return SHA256.HashData(Encoding.UTF8.GetBytes("erental-backup|" + jwt));
    }

    private byte[] Encrypt(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[plain.Length];
        using var aes = new AesGcm(ResolveKey(), TagSize);
        aes.Encrypt(nonce, plain, cipher, tag, Magic);

        var output = new byte[Magic.Length + NonceSize + TagSize + cipher.Length];
        Magic.CopyTo(output, 0);
        nonce.CopyTo(output, Magic.Length);
        tag.CopyTo(output, Magic.Length + NonceSize);
        cipher.CopyTo(output, Magic.Length + NonceSize + TagSize);
        return output;
    }

    private byte[] Decrypt(byte[] data)
    {
        var header = Magic.Length + NonceSize + TagSize;
        if (data.Length <= header || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidOperationException("不是有效的加密备份文件（或为旧版明文 JSON，请手工处理）");

        var nonce = data.AsSpan(Magic.Length, NonceSize).ToArray();
        var tag = data.AsSpan(Magic.Length + NonceSize, TagSize).ToArray();
        var cipher = data.AsSpan(header).ToArray();
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(ResolveKey(), TagSize);
        //认证失败会抛 CryptographicException
        aes.Decrypt(nonce, cipher, tag, plain, Magic);
        return plain;
    }


    private string ResolvePath(string fileName)
    {
        var safe = Path.GetFileName(fileName);
        if (!string.Equals(safe, fileName, StringComparison.Ordinal) || !safe.EndsWith(".erbk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("备份文件名不合法");
        var path = Path.Combine(Directory, safe);
        if (!File.Exists(path)) throw new FileNotFoundException("备份文件不存在", safe);
        return path;
    }

    ///<summary>按保留份数清理：只删 backup_ 前缀，恢复前快照最多留 3 份</summary>
    private void ApplyRetention()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            var all = new DirectoryInfo(Directory).GetFiles("*.erbk")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();

            var normal = all.Where(f => f.Name.StartsWith(FilePrefix, StringComparison.Ordinal)).ToList();
            foreach (var old in normal.Skip(KeepCount)) old.Delete();

            var safety = all.Where(f => f.Name.StartsWith(SafetyPrefix, StringComparison.Ordinal)).ToList();
            foreach (var old in safety.Skip(3)) old.Delete();
        }
        catch (Exception ex)
        {
            //清理失败不影响刚生成的备份，只记日志
            _logger.LogWarning(ex, "清理过期备份失败");
        }
    }
}

///<summary>每日定时备份</summary>
public class BackupSchedulerService(
    IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<BackupSchedulerService> logger)
    : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        //启动后先等 1 分钟
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                var next = NextRun(now);
                var wait = next - now;
                logger.LogInformation("下次自动备份时间：{Next:yyyy-MM-dd HH:mm}", next);
                await Task.Delay(wait, stoppingToken);

                using var scope = scopeFactory.CreateScope();

                //多实例部署时只有抢到 MySQL 命名锁的那台执行，其余实例让位
                var locks = scope.ServiceProvider.GetRequiredService<DbLockService>();
                await using var handle = await locks.TryAcquireAsync("erental_backup", TimeSpan.Zero, stoppingToken);
                if (handle is null)
                {
                    logger.LogInformation("自动备份已由其他实例执行，本实例跳过本轮");
                    continue;
                }

                var backup = scope.ServiceProvider.GetRequiredService<BackupService>();
                var result = await backup.CreateAsync(ct: stoppingToken);
                logger.LogInformation("自动备份完成：{File}（{Size} KB，耗时 {Elapsed} ms）",
                    result.FileName, result.SizeBytes / 1024, result.ElapsedMs);

                //备份是分钟级长任务，是最容易撞上「连接被 wait_timeout 掐掉、锁静默消失」的场景
                if (!await handle.IsHeldAsync())
                {
                    logger.LogError(
                        "命名锁 erental_backup 在备份期间丢失（连接可能已失效）：本次备份可能与另一实例重叠，" +
                        "请核对备份目录 {Dir} 是否出现同一时刻的重复文件",
                        backup.Directory);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                //失败不能退出循环
                logger.LogError(ex, "自动备份失败，{Delay} 分钟后重试", RetryDelay.TotalMinutes);
                try { await Task.Delay(RetryDelay, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    ///<summary>计算下一个执行时刻</summary>
    private DateTime NextRun(DateTime now)
    {
        var raw = config["Backup:DailyAt"];
        if (!TimeOnly.TryParse(raw, CultureInfo.InvariantCulture, out var at))
            at = new TimeOnly(2, 30);

        var today = now.Date.Add(at.ToTimeSpan());
        return today > now ? today : today.AddDays(1);
    }
}

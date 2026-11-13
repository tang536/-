using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace ERental.Data;

/// <summary>模型 ↔ 数据库的列级差异体检（安全网）</summary>
public static class SchemaDrift
{
    /// <summary>体检并自愈可空列</summary>
    public static void Reconcile(AppDbContext db)
    {
        var existing = LoadColumns(db);
        var added = new List<string>();
        var manual = new List<string>();

        // 这里只保留「遍历模型 → 逐列体检 → 汇总输出」的骨架，
        // 单列的判断与执行拆到 InspectProperty，避免所有分支挤在同一方法里。
        foreach (var entity in db.Model.GetEntityTypes())
        {
            // 视图映射也带「表名」外观，但 ALTER TABLE 对视图无效；
            // 视图由 SchemaObjects 安装器统一维护，这里必须跳过。
            if (entity.GetViewName() is not null) continue;

            var table = entity.GetTableName();
            if (string.IsNullOrEmpty(table)) continue;   // 无表映射的实体不参与补列

            foreach (var prop in entity.GetProperties())
                InspectProperty(db, table, prop, existing, (added, manual));
        }

        ReportDrift(added, manual);
    }

    /// <summary>体检单个属性的列，必要时补列或记入人工清单</summary>
    /// <param name="db">用于执行补列 DDL 的上下文</param>
    /// <param name="table">属性所属的表名</param>
    /// <param name="prop">要核对的 EF 属性映射</param>
    /// <param name="existing">已存在的 (表, 列)，补列成功后同步写入，避免后续重名判断漏掉</param>
    /// <param name="report">汇总清单：Added 为已自动补齐，Manual 为需人工确认</param>
    private static void InspectProperty(
        AppDbContext db,
        string table,
        IProperty prop,
        HashSet<(string, string)> existing,
        (List<string> Added, List<string> Manual) report)
    {
        var column = prop.GetColumnName();
        if (string.IsNullOrEmpty(column)) return;              // 无列映射的属性（如导航/影子属性）不参与
        if (prop.IsPrimaryKey()) return;                       // 主键必然存在
        if (existing.Contains((table, column))) return;

        var storeType = ResolveStoreType(prop);
        if (storeType == null)
        {
            report.Manual.Add($"`{table}`.`{column}`：无法从模型推断存储类型，请手工补列");
            return;
        }

        // DDL 交给 EF 迁移生成器按当前数据库提供程序拼（标识符转义、类型写法都由它负责）。
        // 不自己拼串的原因：补列语句里「列类型」位置是必填的，一旦该位置被插值洞占住，
        // IDE 的 SQL 语言注入会把洞替换成占位标识符，于是误报「应为类型」的语法错误。
        // ⚠️ IsNullable 必须原样传 prop.IsNullable：曾经写成 !prop.IsNullable，
        // 可空列被生成成 NOT NULL——往有数据的表加列直接失败
        // （Incorrect datetime value: '0000-00-00'），空表则静默建成非空列，两种都错。
        var ddl = BuildAddColumnDdl(db, table, column, prop.ClrType, storeType, prop.IsNullable);
        if (ddl == null)
        {
            report.Manual.Add($"{table}.{column}（{storeType}，{(prop.IsNullable ? "可空" : "NOT NULL")}）：未能生成补列语句，请手工补列");
            return;
        }

        if (!prop.IsNullable)
        {
            // 非空列：给字段挑默认值属于业务决策，这里只把可直接执行的 ALTER 打出来交给人工
            report.Manual.Add($"{ddl}   —— 模型要求 NOT NULL，请确认默认值后执行");
            return;
        }

        TryApplyAddColumn(db, table, column, storeType, ddl, existing, report);
    }

    /// <summary>执行补列 DDL 并登记结果；失败不抛出，转人工清单（体检本身不该中断启动流程）</summary>
    private static void TryApplyAddColumn(
        AppDbContext db,
        string table,
        string column,
        string storeType,
        string ddl,
        HashSet<(string, string)> existing,
        (List<string> Added, List<string> Manual) report)
    {
        try
        {
            db.Database.ExecuteSqlRaw(ddl);
            report.Added.Add($"{table}.{column} {storeType}");
            existing.Add((table, column));
        }
        catch (Exception ex)
        {
            report.Manual.Add($"{ddl}   —— 自动执行失败：{ex.Message}");
        }
    }

    /// <summary>输出体检结论：自动补齐的列与需要人工确认的项</summary>
    private static void ReportDrift(List<string> added, List<string> manual)
    {
        if (added.Count > 0) Console.WriteLine($"[结构自愈] 自动补齐 {added.Count} 个缺失的可空列：{string.Join("、", added)}");

        if (manual.Count > 0)
        {
            Console.WriteLine($"[结构差异] {manual.Count} 处需要人工确认（未自动变更）：");
            foreach (var item in manual) Console.WriteLine("  · " + item);
        }
    }

    /// <summary>读取当前库已有的「表</summary>
    private static HashSet<(string, string)> LoadColumns(AppDbContext db)
    {
        var set = new HashSet<(string, string)>();
        using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT TABLE_NAME, COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()";
        if (cmd.Connection!.State != ConnectionState.Open) cmd.Connection.Open();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
            set.Add((reader.GetString(0).ToLowerInvariant(), reader.GetString(1).ToLowerInvariant()));
        }
        return set;
    }

    /// <summary>用 EF 迁移 SQL 生成器拼一条「补列」DDL —— 与当前数据库提供程序同源， 标识符转义（MySQL 反引号…</summary>
    private static string? BuildAddColumnDdl(AppDbContext db, string table, string column, Type clrType, string storeType, bool nullable)
    {
        try
        {
            var operations = db.GetService<IMigrationsSqlGenerator>().Generate(
            [
                new AddColumnOperation
                {
                    Table = table,
                    Name = column,
                    ColumnType = storeType,
                    ClrType = clrType,
                    IsNullable = nullable
                }
            ]);
            var sql = string.Join(" ", operations.Select(o => o.CommandText).Where(t => !string.IsNullOrWhiteSpace(t)));
            return string.IsNullOrWhiteSpace(sql) ? null : sql;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[结构自愈] 生成 {table}.{column} 的补列语句失败，转人工处理：{ex.Message}");
            return null;
        }
    }

    /// <summary>模型属性 → MySQL 存储类型</summary>
    private static string? ResolveStoreType(IProperty prop)
    {
        var configured = prop.GetColumnType();
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        var clr = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
        if (clr == typeof(string)) return $"VARCHAR({prop.GetMaxLength() ?? 255})";
        if (clr == typeof(int)) return "INT";
        if (clr == typeof(long)) return "BIGINT";
        if (clr == typeof(short)) return "SMALLINT";
        if (clr == typeof(byte)) return "TINYINT UNSIGNED";
        if (clr == typeof(bool)) return "TINYINT(1)";
        if (clr == typeof(decimal)) return "DECIMAL(18,2)";
        if (clr == typeof(double)) return "DOUBLE";
        if (clr == typeof(float)) return "FLOAT";
        if (clr == typeof(DateTime)) return "DATETIME";
        if (clr == typeof(byte[])) return "BLOB";
        if (clr == typeof(Guid)) return "CHAR(36)";
        return clr.IsEnum ? "INT" : null;
    }
}

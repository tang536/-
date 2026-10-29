using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace ERental.Data;

/// <summary>
/// 模型 ↔ 数据库的列级差异体检（安全网）。
///
/// 为什么需要它：EF 的 <c>EnsureCreated()</c> 只在「数据库不存在」时建表，对已存在的老库
/// **既不补表也不补列**；而任意一次 EF 查询都会按实体模型投影出全部列，只要库里少了其中一列，
/// 查询就抛 <c>Unknown column 'x' in 'field list'</c>。这类异常发生在 Program.cs 的启动路径上，
/// 会让整个后端进程起不来（本项目就踩过一次：customer.account_status）。
///
/// 处理策略：
///   · 「模型里有、库里没有」的**可空列** → 自动补上。往非空表里加可空列永远安全，
///     不需要默认值，也不会重写数据，MySQL 8 下是瞬时 DDL。
///   · **非空列** → 只打印可直接执行的 ALTER 建议，不擅自补默认值（给字段选默认值属于业务决策）。
///   · 建议语句由 EF 迁移 SQL 生成器产出（与当前数据库提供程序同源），不手工拼 SQL 字符串。
///   · 无法从模型推断存储类型的属性 → 同样只打印，交给人工。
/// 每一步都会打印日志；全部动作幂等，重复启动不会产生副作用。
/// </summary>
public static class SchemaDrift
{
    /// <summary>体检并自愈可空列；无法自动修复的条目打印到启动日志，供人工处理。</summary>
    public static void Reconcile(AppDbContext db)
    {
        var existing = LoadColumns(db);
        var added = new List<string>();
        var manual = new List<string>();

        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (string.IsNullOrEmpty(table)) continue;

            foreach (var prop in entity.GetProperties())
            {
                var column = prop.GetColumnName();
                if (string.IsNullOrEmpty(column)) continue;
                if (prop.IsPrimaryKey()) continue;                       // 主键必然存在
                if (existing.Contains((table, column))) continue;

                var storeType = ResolveStoreType(prop);
                if (storeType == null)
                {
                    manual.Add($"`{table}`.`{column}`：无法从模型推断存储类型，请手工补列");
                    continue;
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
                    manual.Add($"{table}.{column}（{storeType}，{(prop.IsNullable ? "可空" : "NOT NULL")}）：未能生成补列语句，请手工补列");
                    continue;
                }

                if (!prop.IsNullable)
                {
                    // 非空列：给字段挑默认值属于业务决策，这里只把可直接执行的 ALTER 打出来交给人工
                    manual.Add($"{ddl}   —— 模型要求 NOT NULL，请确认默认值后执行");
                    continue;
                }

                try
                {
                    db.Database.ExecuteSqlRaw(ddl);
                    added.Add($"{table}.{column} {storeType}");
                    existing.Add((table, column));
                }
                catch (Exception ex)
                {
                    manual.Add($"{ddl}   —— 自动执行失败：{ex.Message}");
                }
            }
        }

        if (added.Count > 0)
            Console.WriteLine($"[结构自愈] 自动补齐 {added.Count} 个缺失的可空列：{string.Join("、", added)}");

        if (manual.Count > 0)
        {
            Console.WriteLine($"[结构差异] {manual.Count} 处需要人工确认（未自动变更）：");
            foreach (var item in manual) Console.WriteLine("  · " + item);
        }
    }

    /// <summary>读取当前库已有的「表.列」，键统一小写（MySQL 在 Windows 上大小写不敏感）。</summary>
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

    /// <summary>
    /// 用 EF 迁移 SQL 生成器拼一条「补列」DDL —— 与当前数据库提供程序同源，
    /// 标识符转义（MySQL 反引号）、类型写法都交给提供程序，避免自己拼串。
    /// 生成失败返回 <c>null</c>，由调用方降级为人工提示：启动自愈不能因为拼不出 SQL 就中断。
    /// </summary>
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

    /// <summary>模型属性 → MySQL 存储类型。优先用模型显式配置的类型，其次按 CLR 类型 + 长度推断。</summary>
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

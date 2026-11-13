using System.Data.Common;
using ERental.Data;
using Microsoft.EntityFrameworkCore;

namespace ERental.Tests;

/// <summary>
/// MySQL 集成测试用的事实标记：只有显式配置了测试服务器连接串才执行。
/// 连接串指向「服务器级」账号（不要带生产库名），测试会另建一个随机命名的临时库，
/// 跑完即删，不会触碰开发/演示数据。
/// <para>设置方式：<c>$env:ERENTAL_TEST_CONNECTION="Server=127.0.0.1;Port=3306;User=root;Password=***;SslMode=Disabled"</c></para>
/// </summary>
public sealed class MySqlFactAttribute : FactAttribute
{
    public MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ERENTAL_TEST_CONNECTION")))
            Skip = "未设置 ERENTAL_TEST_CONNECTION，跳过 MySQL 集成测试";
    }
}

/// <summary>一次集成测试的临时数据库：建库 → 建表 → 安装视图/触发器 → 用完后 DROP</summary>
internal sealed class TestDatabase : IDisposable
{
    public AppDbContext Db { get; }

    private readonly string _databaseName;
    private readonly string _serverConnectionString;

    private TestDatabase(AppDbContext db, string databaseName, string serverConnectionString)
    {
        Db = db;
        _databaseName = databaseName;
        _serverConnectionString = serverConnectionString;
    }

    public static TestDatabase Create()
    {
        var server = Environment.GetEnvironmentVariable("ERENTAL_TEST_CONNECTION")
            ?? throw new InvalidOperationException("未设置 ERENTAL_TEST_CONNECTION");

        var name = $"erental_test_{Guid.NewGuid():N}"[..30];
        ExecuteOnServer(server, $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");

        var connectionString = WithDatabase(server, name);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySQL(connectionString)
            .AddInterceptors(new MySqlSessionTimeZoneInterceptor())
            .Options;
        var db = new AppDbContext(options);
        // 建表：EF 模型一次建全。视图映射用 ToView，不会建出同名空表。
        db.Database.EnsureCreated();
        // 安装 objects.sql：视图/函数/触发器/存储过程都按生产同一份脚本安装。
        SchemaObjects.EnsureInstalled(db);

        return new TestDatabase(db, name, server);
    }

    public void Dispose()
    {
        Db.Dispose();
        ExecuteOnServer(_serverConnectionString, $"DROP DATABASE IF EXISTS `{_databaseName}`");
    }

    private static void ExecuteOnServer(string serverConnectionString, string sql)
    {
        var adminConnectionString = WithDatabase(serverConnectionString, "mysql");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySQL(adminConnectionString)
            .Options;
        using var adminDb = new AppDbContext(options);
        using var connection = DbConnectionFactory.CreateDedicated(adminDb);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string WithDatabase(string connectionString, string database)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        builder["Database"] = database;
        return builder.ConnectionString;
    }
}

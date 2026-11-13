using System.Data.Common;
using ERental.Data;
using Microsoft.EntityFrameworkCore;

namespace ERental.Tests;

/// <summary>时区口径自检、脚本拆分、独立连接属性</summary>
public class InfrastructureTests
{
    [Theory]
    [InlineData(28800, 28800, true)]
    [InlineData(28800, 28799, true)]
    [InlineData(28800, 28861, false)]
    [InlineData(-18000, -18000, true)]
    [InlineData(0, 28800, false)]
    public void DatabaseClock_OffsetAlignment(int appOffset, int dbOffset, bool expected)
    {
        Assert.Equal(expected, DatabaseClock.IsAligned(appOffset, dbOffset));
    }

    [Fact]
    public void DatabaseClock_ToleranceIsOneMinute()
    {
        Assert.Equal(60, DatabaseClock.ToleranceSeconds);
        Assert.True(DatabaseClock.IsAligned(28800, 28800 + 60));
        Assert.False(DatabaseClock.IsAligned(28800, 28800 + 61));
    }

    [Fact]
    public void SqlScript_SplitsByCustomDelimiterAndDropsComments()
    {
        const string script = """
            -- 文件头
            DELIMITER ;;
            CREATE TABLE a (id INT);;
            DELIMITER $$
            CREATE TRIGGER trg BEFORE INSERT ON a
            FOR EACH ROW
            BEGIN
              SET NEW.id = 1;
            END$$
            DELIMITER ;
            INSERT INTO a VALUES (2);
            """;

        var statements = SqlScript.Split(script);

        Assert.Equal(3, statements.Count);
        Assert.Contains("CREATE TABLE a", statements[0], StringComparison.Ordinal);
        Assert.DoesNotContain("DELIMITER", statements[0], StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("CREATE TRIGGER trg", statements[1], StringComparison.Ordinal);
        Assert.Contains("END", statements[1], StringComparison.Ordinal);
        Assert.StartsWith("INSERT INTO a", statements[2], StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaObjects_EmbeddedScriptHasNoTemporaryTable()
    {
        var sql = SqlScriptTestHelper.LoadEmbeddedObjectsSql();

        Assert.DoesNotContain("CREATE TEMPORARY TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#tmp", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sp_cancel_expired_pending_orders", sql, StringComparison.Ordinal);
        Assert.Contains("v_order_overview", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DbConnectionFactory_DisablesPooling()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySQL("Server=127.0.0.1;Port=3306;User=test;Password=test;Database=test;Pooling=true")
            .Options;
        using var db = new AppDbContext(options);
        using var connection = DbConnectionFactory.CreateDedicated(db);

        var builder = new DbConnectionStringBuilder { ConnectionString = connection.ConnectionString };
        Assert.Equal("False", Convert.ToString(builder["Pooling"]), ignoreCase: true);
    }

    [Fact]
    public void DbConnectionFactory_UsesDriverPrototype()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySQL("Server=127.0.0.1;Port=3306;User=test;Password=test;Database=test")
            .Options;
        using var db = new AppDbContext(options);
        using var connection = DbConnectionFactory.CreateDedicated(db);

        Assert.Equal(db.Database.GetDbConnection().GetType(), connection.GetType());
    }
}

internal static class SqlScriptTestHelper
{
    public static string LoadEmbeddedObjectsSql()
    {
        using var stream = typeof(SchemaObjects).Assembly.GetManifestResourceStream("ERental.database.objects.sql")
            ?? throw new InvalidOperationException("缺少嵌入资源 ERental.database.objects.sql");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

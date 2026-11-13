using System.Text;
using ERental.Data;

namespace ERental.Tests;

/// <summary>objects.sql 拆分与对象清单的回归测试：安装器依赖这些约定</summary>
public class SqlScriptTests
{
    private const string ObjectsResource = "ERental.database.objects.sql";

    [Fact]
    public void Split_RemovesDelimiterDirectivesAndKeepsRoutineBody()
    {
        const string script = """
            -- 文件头注释
            CREATE VIEW `v_demo` AS SELECT 1;
            DELIMITER $$
            CREATE PROCEDURE `p_demo`()
            BEGIN
              SELECT 1;
            END$$
            DELIMITER ;
            """;

        var statements = SqlScript.Split(script);

        Assert.Equal(2, statements.Count);
        Assert.Contains("CREATE VIEW", statements[0], StringComparison.Ordinal);
        Assert.Contains("CREATE PROCEDURE", statements[1], StringComparison.Ordinal);
        Assert.Contains("SELECT 1;", statements[1], StringComparison.Ordinal);
        Assert.DoesNotContain("DELIMITER", statements[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Split_IgnoresCommentOnlyTail()
    {
        const string script = """
            CREATE VIEW `v_demo` AS SELECT 1;
            -- 结尾只有注释
            """;

        var statements = SqlScript.Split(script);

        Assert.Single(statements);
    }

    [Fact]
    public void ObjectsScript_DeclaresExpectedObjectCounts()
    {
        var statements = SqlScript.Split(LoadObjectsSql());

        Assert.Equal(35, statements.Count);
        Assert.Equal(3, statements.Count(s => s.Contains("CREATE OR REPLACE", StringComparison.Ordinal) && s.Contains(" VIEW ", StringComparison.Ordinal)));
        Assert.Equal(3, statements.Count(s => s.Contains("CREATE FUNCTION", StringComparison.Ordinal)));
        Assert.Equal(7, statements.Count(s => s.Contains("CREATE TRIGGER", StringComparison.Ordinal)));
        Assert.Equal(6, statements.Count(s => s.Contains("CREATE PROCEDURE", StringComparison.Ordinal)));
    }

    [Fact]
    public void ObjectsScript_ContainsEveryObjectUsedByInstaller()
    {
        var statements = SqlScript.Split(LoadObjectsSql());
        var all = string.Join('\n', statements);

        string[] names =
        [
            "v_order_overview",
            "v_settlement_bill",
            "v_vehicle_maintenance_due",
            "fn_effective_daily_rent",
            "fn_member_discount",
            "fn_billable_hours",
            "trg_order_status_transition",
            "trg_order_terminal_release",
            "trg_renewal_sync_lease",
            "trg_payment_paid_insert",
            "trg_payment_paid_update",
            "trg_message_receiver_insert",
            "trg_message_receiver_update",
            "sp_claim_vehicle",
            "sp_release_vehicle",
            "sp_cancel_order",
            "sp_cancel_pending_order",
            "sp_apply_renewal",
            "sp_cancel_expired_pending_orders"
        ];

        foreach (var name in names)
            Assert.Contains(name, all, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpiredOrderProcedure_DoesNotNeedTemporaryTablePrivilege()
    {
        var statements = SqlScript.Split(LoadObjectsSql());
        var procedure = Assert.Single(statements, s => s.Contains("CREATE PROCEDURE `sp_cancel_expired_pending_orders`", StringComparison.Ordinal));

        Assert.DoesNotContain("TEMPORARY TABLE", procedure, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tmp_erental_expired_orders", procedure, StringComparison.Ordinal);
    }

    private static string LoadObjectsSql()
    {
        var assembly = typeof(AppDbContext).Assembly;
        using var stream = assembly.GetManifestResourceStream(ObjectsResource)
            ?? throw new InvalidOperationException($"找不到嵌入资源 {ObjectsResource}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

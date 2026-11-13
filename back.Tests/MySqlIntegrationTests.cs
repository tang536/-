using ERental.Data;
using ERental.Models;
using ERental.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERental.Tests;

/// <summary>
/// 真实 MySQL 集成测试：每个用例独立建库、跑完即删，绝不复用开发/演示库。
/// 覆盖此前只能靠手工烟测的两条写路径：盘点报表生成事务、结算账单视图。
/// </summary>
public class MySqlIntegrationTests
{
    [MySqlFact]
    public void DatabaseClock_SessionMatchesLocalTimeZone()
    {
        using var testDb = TestDatabase.Create();
        DatabaseClock.EnsureSessionMatchesLocalTime(testDb.Db);
    }

    [MySqlFact]
    public void SchemaObjects_IsIdempotentAndReleasesItsOwnLock()
    {
        using var testDb = TestDatabase.Create();

        // 第二次安装：哈希一致 + 对象齐全，应直接返回，不重复执行 DDL
        SchemaObjects.EnsureInstalled(testDb.Db);
        SchemaObjects.EnsureInstalled(testDb.Db);

        // 安装锁必须已释放：另一条独立连接能立刻拿到同名锁
        using var connection = DbConnectionFactory.CreateDedicated(testDb.Db);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT GET_LOCK('erental:schema_objects', 0)";
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
        cmd.CommandText = "SELECT RELEASE_LOCK('erental:schema_objects')";
        cmd.ExecuteScalar();
    }

    [MySqlFact]
    public async Task SchemaObjects_RefusesToInstallInsideTransaction()
    {
        using var testDb = TestDatabase.Create();
        await using var tx = await testDb.Db.Database.BeginTransactionAsync();

        var ex = Assert.Throws<InvalidOperationException>(() => SchemaObjects.EnsureInstalled(testDb.Db));
        Assert.Contains("事务", ex.Message, StringComparison.Ordinal);
        await tx.RollbackAsync();
    }

    [MySqlFact]
    public async Task CheckService_GenerateReport_PersistsReportAndBackfillsEveryDetail()
    {
        using var testDb = TestDatabase.Create();
        var db = testDb.Db;

        var vehicle = Vehicle.Create(
            ("TEST-PD-001", null, "测试品牌"),
            (null, 3000m),
            (60m, 500m),
            (VehicleStatuses.Idle, null));
        db.Vehicles.Add(vehicle);
        await db.SaveChangesAsync();

        var service = new CheckService(db);
        var matching = await service.AddCheck(vehicle.VehicleId, VehicleStatuses.Idle, null);
        var difference = await service.AddCheck(vehicle.VehicleId, VehicleStatuses.Maintenance, "现场发现车辆在维保");

        var report = await service.GenerateReport(DateTime.Now.Date);

        Assert.Equal(2, report.TotalCount);
        Assert.Equal(1, report.MatchCount);
        Assert.Equal(1, report.DiffCount);
        Assert.True(report.ReportId > 0);
        Assert.StartsWith("RPT", report.ReportNo, StringComparison.Ordinal);

        var details = await db.CarCheckLists.AsNoTracking().OrderBy(c => c.CheckId).ToListAsync();
        Assert.Equal(2, details.Count);
        Assert.All(details, d => Assert.Equal(report.ReportId, d.ReportId));
        Assert.Equal(matching.CheckId, details[0].CheckId);
        Assert.Equal(difference.CheckId, details[1].CheckId);
        Assert.NotEqual(details[0].ActualStatus, details[1].ActualStatus);
    }

    [MySqlFact]
    public async Task SettlementService_GetAll_ReadsViewAndMapsSupplementState()
    {
        using var testDb = TestDatabase.Create();
        var db = testDb.Db;

        var vehicle = Vehicle.Create(
            ("TEST-JS-001", null, "测试品牌"),
            (null, 3000m),
            (60m, 500m),
            (VehicleStatuses.Idle, null));
        var customer = Customer.WalkIn("测试顾客", "110101199001011234", "13800000000");
        db.Vehicles.Add(vehicle);
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var order = RentalOrder.Create(
            ("TEST-JS-ORDER-001", customer.CustomerId, vehicle.VehicleId, null),
            (DateTime.Now.AddDays(-1), DateTime.Now),
            (60m, 500m),
            "已完结");
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var settle = SettleRecord.Create(
            (order.OrderId, null),
            (60m, 20m, 0m),
            (480m, 0m, "集成测试结算"),
            DateTime.Now);
        db.SettleRecords.Add(settle);
        await db.SaveChangesAsync();

        // 结算列表从 GetAll 改成服务端分页（GetPaged），构造参数也多了 logs / images 等依赖；
        // 本用例只验证结算账单视图的字段口径，其余依赖传 null 即可，不要让它们真的被调用。
        var service = new SettlementService(
            db, null!, null!, null!, null!, null!, NullLogger<SettlementService>.Instance);
        var page = await service.GetPaged(null, null, 1, 50);
        var rows = page.Items;

        var row = Assert.Single(rows);
        Assert.Equal(order.OrderNo, row.RentalOrder!.OrderNo);
        Assert.Equal(vehicle.VehicleNo, row.RentalOrder.VehicleNo);
        Assert.Equal(customer.FullName, row.RentalOrder.CustomerName);
        Assert.Equal(60m, row.ActualRent);
        Assert.Equal(20m, row.OverdueFee);
        Assert.Equal(480m, row.RefundAmount);
        Assert.Equal(0m, row.SupplementAmount);
        Assert.True(row.SupplementCollected);
        Assert.Equal("集成测试结算", row.Remark);
    }

    [MySqlFact]
    public async Task OrderOverviewView_IsOverdueUsesDatabaseNowWithSessionTimeZone()
    {
        using var testDb = TestDatabase.Create();
        var db = testDb.Db;

        var vehicle = Vehicle.Create(
            ("TEST-YQ-001", null, "测试品牌"),
            (null, 3000m),
            (60m, 500m),
            (VehicleStatuses.Rented, null));
        var customer = Customer.WalkIn("逾期顾客", "110101199001011234", "13800000000");
        db.Vehicles.Add(vehicle);
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var order = RentalOrder.Create(
            ("TEST-YQ-ORDER-001", customer.CustomerId, vehicle.VehicleId, null),
            (DateTime.Now.AddHours(-26), DateTime.Now.AddHours(-2)),
            (60m, 500m),
            "租赁中");
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();

        var view = await db.OrderOverviews.AsNoTracking()
            .SingleAsync(o => o.OrderId == order.OrderId);

        // 会话时区与应用一致时，2 小时前到期的订单必须被判为逾期；
        // billable_hours 按 1 小时向上取整，至少 26 小时。
        Assert.Equal(1, view.IsOverdue);
        Assert.True(view.BillableHours >= 25);
    }
}

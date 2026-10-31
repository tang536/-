using System.Data;
using System.Data.Common;
using ERental.Data;
using ERental.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERental.Services;

/// <summary>数据库行级悲观锁（SELECT </summary>
public static class DbRowLock
{
    /// <summary>单次等锁上限（秒）</summary>
    public const int LockWaitTimeoutSeconds = 5;

    /// <summary>「待支付」订单状态值</summary>
    private const string PendingPayStatus = "待支付";

    /// <summary>事务内的会话级设置：把等锁上限压到 </summary>
    public static async Task PrepareAsync(AppDbContext db, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(
            $"SET SESSION innodb_lock_wait_timeout = {LockWaitTimeoutSeconds}", ct);
    }

    /// <summary>锁住一张订单（按主键）</summary>
    public static async Task<RentalOrder?> LockOrderAsync(
        AppDbContext db, int orderId, CancellationToken ct = default)
    {
        var hit = await LockScalarAsync(db,
            "SELECT `order_id` FROM `rental_order` WHERE `order_id` = @id FOR UPDATE",
            ("@id", orderId), ct);
        if (hit is null) return null;
        // ⚠ 必须 Reload：EF 查询遇到「已被跟踪」的实体会返回跟踪器里的那个实例，
        // 并且**不用数据库值覆盖它的属性**。也就是说，若本次请求在事务外先读过这张订单，
        // 上面那句 FOR UPDATE 虽然拿到了锁，下面这句却会返回**加锁前的旧状态**，
        // 状态判定照样基于旧值——锁白加，而且不报错。
        // ReloadAsync 会让 EF 重新查一次并覆盖当前值/原始值，拿到的是锁内真实状态。
        var order = await db.RentalOrders.FirstOrDefaultAsync(o => o.OrderId == orderId, ct);
        if (order is null) return null;
        await db.Entry(order).ReloadAsync(ct);
        return order;
    }

    /// <summary>锁住一张订单（按订单号）</summary>
    public static async Task<RentalOrder?> LockOrderByNoAsync(
        AppDbContext db, string orderNo, CancellationToken ct = default)
    {
        var hit = await LockScalarAsync(db,
            "SELECT `order_id` FROM `rental_order` WHERE `order_no` = @no FOR UPDATE",
            ("@no", orderNo), ct);
        var id = hit is null ? 0 : Convert.ToInt32(hit);
        return id == 0 ? null : await LockOrderAsync(db, id, ct);
    }

    /// <summary>锁住一条支付记录</summary>
    public static async Task<PaymentRecord?> LockPaymentAsync(
        AppDbContext db, int payId, CancellationToken ct = default)
    {
        var hit = await LockScalarAsync(db,
            "SELECT `pay_id` FROM `payment_record` WHERE `pay_id` = @id FOR UPDATE",
            ("@id", payId), ct);
        if (hit is null) return null;
        var pay = await db.PaymentRecords.FirstOrDefaultAsync(p => p.PayId == payId, ct);
        if (pay is null) return null;
        await db.Entry(pay).ReloadAsync(ct);   // 同上：覆盖跟踪器里的旧 refund_amount
        return pay;
    }

    /// <summary>锁住客户档案的一行</summary>
    public static async Task<Customer?> LockCustomerAsync(
        AppDbContext db, int customerId, CancellationToken ct = default)
    {
        var hit = await LockScalarAsync(db,
            "SELECT `customer_id` FROM `customer` WHERE `customer_id` = @id FOR UPDATE",
            ("@id", customerId), ct);
        if (hit is null) return null;
        var c = await db.Customers.FirstOrDefaultAsync(x => x.CustomerId == customerId, ct);
        if (c is null) return null;
        await db.Entry(c).ReloadAsync(ct);     // 同上：覆盖跟踪器里的旧等级/余额
        return c;
    }

    /// <summary>锁住一条系统配置（会员购买意向以 memberpay</summary>
    public static async Task<SystemConfig?> LockConfigAsync(
        AppDbContext db, string key, CancellationToken ct = default)
    {
        var hit = await LockScalarAsync(db,
            "SELECT `config_id` FROM `system_config` WHERE `config_key` = @k FOR UPDATE",
            ("@k", key), ct);
        if (hit is null) return null;
        var cfg = await db.SystemConfigs.FirstOrDefaultAsync(c => c.ConfigKey == key, ct);
        if (cfg is null) return null;
        await db.Entry(cfg).ReloadAsync(ct);   // 同上：覆盖跟踪器里的旧配置值
        return cfg;
    }

    /// <summary>批量锁住「已超时仍未支付」的订单，跳过已被其它线程/实例锁住的行</summary>
    /// 已发起支付的订单专用阈值（更早的时刻）。传 null 时所有超时单一视同仁。
    /// 顾客在收银台输密码的那几十秒里，订单按创建时间算早就"超时"了；
    /// 若此时被后台任务取消，回调到达只能走「需人工退款」分支——
    /// 钱收了、单作废，柜台要手工退一次款。给「已经向通道下过单」的订单宽限一段时间，
    /// 就能把这类两侧都麻烦的情况压到接近零。
    public static async Task<List<RentalOrder>> LockExpiredPendingAsync(
        AppDbContext db, DateTime deadline, int batch,
        DateTime? paidGraceDeadline = null, CancellationToken ct = default)
    {
        // 判定「已发起支付」的依据是 payment_record 上的渠道标记：
        // 下单时该字段是占位值「待定」，顾客点「去支付」时由 PaymentController
        // 更新为真实渠道（支付宝/微信/银行卡）。因此「pay_type <> '待定' 且仍待支付」
        // 恰好等价于「已经在收银台上，但还没回来」。不需要新增列。
        var sql = paidGraceDeadline is null
            ? "SELECT `order_id` FROM `rental_order` " +
              "WHERE `order_status` = @s AND `create_time` < @d " +
              "ORDER BY `order_id` LIMIT @n FOR UPDATE SKIP LOCKED"
            : "SELECT o.`order_id` FROM `rental_order` o " +
              "WHERE o.`order_status` = @s AND o.`create_time` < @d " +
              "  AND (o.`create_time` < @dg " +
              "       OR NOT EXISTS (SELECT 1 FROM `payment_record` p " +
              "                      WHERE p.`order_id` = o.`order_id` " +
              "                        AND p.`pay_status` = '待支付' " +
              "                        AND p.`pay_type` <> '待定')) " +
              "ORDER BY o.`order_id` LIMIT @n FOR UPDATE SKIP LOCKED";

        (string Name, object? Value)[] args;
        if (paidGraceDeadline is null)
            args = [("@s", PendingPayStatus), ("@d", deadline), ("@n", batch)];
        else
            args = [("@s", PendingPayStatus), ("@d", deadline),
                    ("@dg", paidGraceDeadline.Value), ("@n", batch)];

        var ids = await QueryScalarsAsync(db, sql, args, ct);

        if (ids.Count == 0) return [];
        var orders = await db.RentalOrders.Where(o => ids.Contains(o.OrderId)).ToListAsync(ct);
        // 同 LockOrderAsync：本批次可能有一部分已被跟踪过，强制用锁内真实值覆盖
        foreach (var o in orders) await db.Entry(o).ReloadAsync(ct);
        // 按 id 顺序返回，保证多批处理的顺序稳定、便于排查
        return [.. orders.OrderBy(o => o.OrderId)];
    }

    /// <summary>在当前连接上执行一条「返回单值」的加锁查询</summary>
    private static async Task<object?> LockScalarAsync(
        AppDbContext db, string sql, (string Name, object? Value) arg, CancellationToken ct)
        => await ExecuteScalarAsync(db, sql, [arg], ct);

    private static async Task<object?> ExecuteScalarAsync(
        AppDbContext db, string sql, (string Name, object? Value)[] args, CancellationToken ct)
    {
        await using var cmd = await CreateCommandAsync(db, sql, args, ct);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is DBNull ? null : value;
    }

    private static async Task<List<int>> QueryScalarsAsync(
        AppDbContext db, string sql, (string Name, object? Value)[] args, CancellationToken ct)
    {
        await using var cmd = await CreateCommandAsync(db, sql, args, ct);
        var list = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(0)) list.Add(reader.GetInt32(0));
        }
        return list;
    }

    /// <summary>建命令：显式挂到 EF 当前事务上</summary>
    private static async Task<DbCommand> CreateCommandAsync(
        AppDbContext db, string sql, (string Name, object? Value)[] args, CancellationToken ct)
    {
        _ = ct;
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await db.Database.OpenConnectionAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        foreach (var (name, value) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }
}

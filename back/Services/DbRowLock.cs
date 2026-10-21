using System.Data;
using System.Data.Common;
using ERental.Data;
using ERental.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERental.Services;

/// <summary>
/// 数据库**行级悲观锁**（<c>SELECT ... FOR UPDATE</c>）的统一入口。
///
/// ── 为什么需要它（与 <see cref="VehicleLeaseGuard"/> 的分工）──
/// <see cref="VehicleLeaseGuard"/> 解决的是「同一辆车」的争抢，用带条件的单条 UPDATE 就够了。
/// 但订单维度还有一类竞态是条件 UPDATE 表达不了的：
///   · 结算：两个前台同时点「结算」，都先读到「租赁中」，于是各自生成一条结算单、各发起一次退款；
///   · 续租：两个窗口同时续 24 小时，各自读到旧 lease_end，后写覆盖先写 —— 少收 24 小时租金；
///   · 退款：两个财务同时退 ¥100，都读到 refund_amount=0，于是累计只记 100，实际退了 200；
///   · 支付回调：支付宝重复推送同一条通知，两个线程同时判定「待支付」，各入账一次。
/// 这些场景的判定条件分散在多个字段与业务规则里，无法压成一条 UPDATE，
/// 只能靠「先锁住这一行，再读、再判定、再写」，让并发的第二名在原地排队。
///
/// ── 为什么不用 EF 的并发令牌（[ConcurrencyCheck] + version 列）──
/// 乐观并发是「写的时候才发现冲突」，调用方必须自己处理 <c>DbUpdateConcurrencyException</c>
/// 并重做整段业务（重新算价、重新调网关），在退款/结算这类带外部副作用的流程里极难做对；
/// 悲观锁是「进来就排队」，业务代码保持直线结构，语义也更贴近柜台场景（同一张单只该有一个人在操作）。
///
/// ── 使用约束（务必遵守）──
/// ① 必须在**已开启的事务**内调用：锁只在事务期间有效，事务一提交/回滚就自动释放；
/// ② 锁顺序固定（订单 → 支付记录 → 客户），跨流程保持同一顺序才不会互相咬死；
/// ③ 锁等待上限由 <see cref="PrepareAsync"/> 压到 5 秒（MySQL 默认 50 秒），
///    超时抛 1205，由 <see cref="DbConcurrency"/> 判定为可重试，避免请求线程被挂死。
/// </summary>
public static class DbRowLock
{
    /// <summary>
    /// 单次等锁上限（秒）。取 5 秒的依据：柜台业务的临界区都在毫秒级（改状态 + 写一行记录），
    /// 5 秒还没轮到说明前面的持锁者卡住了（例如正在等支付网关退款），此时快速失败让前台重试
    /// 比继续排队更有意义——继续等只会让请求线程与连接池一起被拖住。
    /// </summary>
    public const int LockWaitTimeoutSeconds = 5;

    /// <summary>「待支付」订单状态值。与 OrderService 的状态机保持同一口径。</summary>
    private const string PendingPayStatus = "待支付";

    /// <summary>
    /// 事务内的会话级设置：把等锁上限压到 <see cref="LockWaitTimeoutSeconds"/>。
    /// 必须与业务 SQL 走**同一条连接**（EF 在事务内会复用同一条），否则设置不生效。
    /// </summary>
    public static async Task PrepareAsync(AppDbContext db, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(
            $"SET SESSION innodb_lock_wait_timeout = {LockWaitTimeoutSeconds}", ct);
    }

    /// <summary>
    /// 锁住一张订单（按主键）。返回锁内读到的最新实体；订单不存在返回 <c>null</c>。
    /// 调用方拿到实体后应**只用它**做状态判定，不要再用事务外读到的旧对象。
    /// </summary>
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

    /// <summary>锁住一张订单（按订单号）。支付回调只拿得到 out_trade_no，故提供此重载。</summary>
    public static async Task<RentalOrder?> LockOrderByNoAsync(
        AppDbContext db, string orderNo, CancellationToken ct = default)
    {
        var hit = await LockScalarAsync(db,
            "SELECT `order_id` FROM `rental_order` WHERE `order_no` = @no FOR UPDATE",
            ("@no", orderNo), ct);
        var id = hit is null ? 0 : Convert.ToInt32(hit);
        return id == 0 ? null : await LockOrderAsync(db, id, ct);
    }

    /// <summary>
    /// 锁住一条支付记录。退款必须先在锁内重读 <c>refund_amount</c>，
    /// 否则并发退款的累计金额会互相覆盖（两笔各退 ¥100，账上只记 ¥100）。
    /// </summary>
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

    /// <summary>
    /// 锁住客户档案的一行。会员等级调整（员工改档 / 自助购买入账）共用这一把锁，
    /// 避免「刚买入的等级被一次后台改档覆盖」。
    /// </summary>
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

    /// <summary>
    /// 锁住一条系统配置（会员购买意向以 <c>memberpay.*</c> 键存放在 system_config）。
    /// 支付回调的并发去重依赖它：两个通知同时到达时，第二个会在锁内看到 <c>done</c> 标记而直接返回。
    /// </summary>
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

    /// <summary>
    /// 批量锁住「已超时仍未支付」的订单，跳过已被其它线程/实例锁住的行。
    ///
    /// <c>SKIP LOCKED</c>（MySQL 8.0+）是这件事的关键：多实例部署时，
    /// 两台机器的清理任务同时启动，不跳过的写法会让后到者阻塞在同一批订单上，
    /// 白白消耗等锁时间；跳过之后各自处理各自的一批，天然并行且不会重复取消。
    /// </summary>
    /// <param name="db">数据库上下文（其当前事务即锁的作用域）</param>
    /// <param name="deadline">创建时间早于该值即视为超时</param>
    /// <param name="batch">单批上限，避免一次事务锁住过多行</param>
    /// <param name="ct">取消令牌（不影响已获得的锁）</param>
    public static async Task<List<RentalOrder>> LockExpiredPendingAsync(
        AppDbContext db, DateTime deadline, int batch, CancellationToken ct = default)
    {
        var ids = await QueryScalarsAsync(db,
            "SELECT `order_id` FROM `rental_order` " +
            "WHERE `order_status` = @s AND `create_time` < @d " +
            "ORDER BY `order_id` LIMIT @n FOR UPDATE SKIP LOCKED",
            [("@s", PendingPayStatus), ("@d", deadline), ("@n", batch)], ct);

        if (ids.Count == 0) return [];
        var orders = await db.RentalOrders.Where(o => ids.Contains(o.OrderId)).ToListAsync(ct);
        // 同 LockOrderAsync：本批次可能有一部分已被跟踪过，强制用锁内真实值覆盖
        foreach (var o in orders) await db.Entry(o).ReloadAsync(ct);
        // 按 id 顺序返回，保证多批处理的顺序稳定、便于排查
        return [.. orders.OrderBy(o => o.OrderId)];
    }

    /// <summary>在当前连接上执行一条「返回单值」的加锁查询；无匹配行返回 null。</summary>
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

    /// <summary>
    /// 建命令：显式挂到 EF 当前事务上。不挂的话 Connector/NET 会把它当作事务外的独立语句执行，
    /// <c>FOR UPDATE</c> 拿到的锁随即被自动提交释放，等于没锁。
    /// </summary>
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

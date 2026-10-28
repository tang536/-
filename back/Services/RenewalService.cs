using System.Data;
using ERental.Data;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class RenewalService(AppDbContext db, ILogger<RenewalService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly ILogger<RenewalService> _logger = logger;

    public async Task<List<RenewalRecord>> GetAll(int? orderId = null)
    {
        var q = _db.RenewalRecords.Include(r => r.RentalOrder).AsQueryable();
        if (orderId.HasValue) q = q.Where(r => r.OrderId == orderId);
        return await q.OrderByDescending(r => r.RenewalTime).ToListAsync();
    }

    /// <summary>单次续租小时数上限（需求 4.2.4 订单续租：选择续租时长后核算费用）。</summary>
    private const int MaxRenewalHours = 24 * 365;

    /// <summary>
    /// 续租。计算方式是「在原 lease_end 上累加」，属于典型的**读-改-写**：
    /// 两个窗口同时给同一张单续 24 小时，原实现各自读到同一个旧 lease_end，
    /// 后提交的一方把先提交的结果覆盖掉——顾客实际多用了 48 小时，系统只记 24 小时租金，
    /// 而且两张续租记录的 new_end_time 还都是「旧值 + 24」，对账时看不出问题。
    ///
    /// 升级后：订单行锁把两个请求串起来，第二个拿到锁时读到的是**已加过 24 小时**的新值，
    /// 再累加自己的 24 小时。两条续租记录首尾相接，账实一致。
    /// </summary>
    public async Task<RenewalRecord> Create(int orderId, int hours, int? operatorId = null)
        => await DbConcurrency.RunAsync(
            () => CreateOnce(orderId, hours, operatorId), _logger, "订单续租",
            reset: () => _db.ChangeTracker.Clear());

    private async Task<RenewalRecord> CreateOnce(int orderId, int hours, int? operatorId)
    {
        // 续租时长的合法性必须校验：0 是无效操作，负数会让 LeaseEnd 倒退
        //（等于把租期改短），超大值则会把租期推到数年之后，都是直接的数据损坏。
        // 计费粒度与下单一致按小时，因此这里收的是小时数。
        if (hours <= 0) throw new Exception("续租时长必须大于 0 小时");
        if (hours > MaxRenewalHours) throw new Exception($"单次续租不能超过 {MaxRenewalHours} 小时");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);

            var order = await DbRowLock.LockOrderAsync(_db, orderId)
                        ?? throw new Exception("订单不存在");
            if (order.OrderStatus is not ("已支付" or "租赁中"))
                throw new Exception("订单状态不允许续租");

            // 续租费按订单所租车辆的小时租金核算（车辆未单独定价才回退全局配置），
            // 与下单、结算保持同一口径
            var (_, hourlyRent, _) = await PricingResolver.ResolveHourlyAsync(_db, order.VehicleId);

            var fee = Math.Round(hourlyRent * hours, 2);
            var oldEnd = order.LeaseEnd;
            order.ExtendLease(order.LeaseEnd.AddHours(hours));

            var record = new RenewalRecord
            {
                OrderId = orderId,
                RenewalDays = hours,
                RenewalFee = fee,
                OldEndTime = oldEnd,
                NewEndTime = order.LeaseEnd,
                RenewalTime = DateTime.Now,
                OperatorId = operatorId
            };
            _db.RenewalRecords.Add(record);
            await _db.SaveChangesAsync();

            await tx.CommitAsync();
            return record;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}

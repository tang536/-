using ERental.Data;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class RenewalService(AppDbContext db)
{
    private readonly AppDbContext _db = db;

    public async Task<List<RenewalRecord>> GetAll(int? orderId = null)
    {
        var q = _db.RenewalRecords.Include(r => r.RentalOrder).AsQueryable();
        if (orderId.HasValue) q = q.Where(r => r.OrderId == orderId);
        return await q.OrderByDescending(r => r.RenewalTime).ToListAsync();
    }

    /// <summary>单次续租小时数上限（需求 4.2.4 订单续租：选择续租时长后核算费用）。</summary>
    private const int MaxRenewalHours = 24 * 365;

    public async Task<RenewalRecord> Create(int orderId, int hours, int? operatorId = null)
    {
        var order = await _db.RentalOrders.FindAsync(orderId)
            ?? throw new Exception("订单不存在");
        if (order.OrderStatus is not ("已支付" or "租赁中"))
            throw new Exception("订单状态不允许续租");

        // 续租时长的合法性必须校验：0 是无效操作，负数会让 LeaseEnd 倒退
        //（等于把租期改短），超大值则会把租期推到数年之后，都是直接的数据损坏。
        // 计费粒度与下单一致按小时，因此这里收的是小时数。
        if (hours <= 0) throw new Exception("续租时长必须大于 0 小时");
        if (hours > MaxRenewalHours) throw new Exception($"单次续租不能超过 {MaxRenewalHours} 小时");

        // 续租费按订单所租车辆的小时租金核算（车辆未单独定价才回退全局配置），
        // 与下单、结算保持同一口径
        var (_, hourlyRent, _) = await PricingResolver.ResolveHourlyAsync(_db, order.VehicleId);

        var fee = Math.Round(hourlyRent * hours, 2);
        var oldEnd = order.LeaseEnd;
        order.LeaseEnd = order.LeaseEnd.AddHours(hours);

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
        return record;
    }
}

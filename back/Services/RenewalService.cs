using System.Data;
using ERental.Data;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class RenewalService(AppDbContext db,ILogger<RenewalService> logger)
{
    private readonly AppDbContext _db=db;
    private readonly ILogger<RenewalService> _logger=logger;

    public async Task<List<RenewalRecord>> GetAll(int? orderId=null)
    {
        var q=_db.RenewalRecords.Include(r=>r.RentalOrder).AsQueryable();
        if(orderId.HasValue) q=q.Where(r=>r.OrderId==orderId);
        return await q.OrderByDescending(r=>r.RenewalTime).ToListAsync();
    }

    ///<summary>单次续租小时数上限</summary>
    private const int MaxRenewalHours=24*365;

    public async Task<RenewalRecord> Create(int orderId,int hours,int? operatorId=null)=>await DbConcurrency.RunAsync(()=>CreateOnce(orderId,hours,operatorId),_logger,"订单续租",reset: ()=>_db.ChangeTracker.Clear());

    private async Task<RenewalRecord> CreateOnce(int orderId,int hours,int? operatorId)
    {
        //续租时长的合法性必须校验：0 是无效操作，负数会让 LeaseEnd 倒退（等于把租期改短）
        if(hours<=0) throw new BusinessException("续租时长必须大于 0 小时");
        if(hours>MaxRenewalHours) throw new BusinessException($"单次续租不能超过 {MaxRenewalHours} 小时");

        await using var tx=await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);

            var order=await DbRowLock.LockOrderAsync(_db,orderId)??throw new BusinessException("订单不存在");
            if(order.OrderStatus is not("已支付" or "租赁中")) throw new BusinessException("订单状态不允许续租");

            //续租费按订单所租车辆的小时租金核算（车辆未单独定价才回退全局配置），与下单、结算保持同一口径
            var(_,hourlyRent,_)=await PricingResolver.ResolveHourlyAsync(_db,order.VehicleId);

            var fee=Math.Round(hourlyRent*hours,2);
            var oldEnd=order.LeaseEnd;
            order.ExtendLease(order.LeaseEnd.AddHours(hours));

            var record=RenewalRecord.Create(orderId,hours,fee,oldEnd,order.LeaseEnd,operatorId,DateTime.Now);
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

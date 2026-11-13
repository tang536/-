using ERental.Data;
using ERental.DTOs;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class RenewalService(AppDbContext db, ILogger<RenewalService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly ILogger<RenewalService> _logger = logger;

    ///<summary>续租记录列表：只投影前端展示需要的列</summary>
    public async Task<List<RenewalDto>> GetAll(int? orderId = null)
    {
        var q = _db.RenewalRecords.AsNoTracking();
        if (orderId.HasValue) q = q.Where(r => r.OrderId == orderId);
        return await q.OrderByDescending(r => r.RenewalTime)
            .Select(r => new RenewalDto
            {
                RenewalId = r.RenewalId,
                OrderId = r.OrderId,
                RenewalDays = r.RenewalDays,
                RenewalFee = r.RenewalFee,
                OldEndTime = r.OldEndTime,
                NewEndTime = r.NewEndTime,
                RenewalTime = r.RenewalTime
            })
            .ToListAsync();
    }

    ///<summary>
    /// 续租：按天顺延。锁单、状态校验、计费、写续租记录、同步到期时间
    /// 全部由 sp_apply_renewal 在库内同一事务完成。
    /// </summary>
    public Task<RenewalDto> Create(int orderId, int days, int? operatorId = null) =>
        DbConcurrency.RunAsync(
            () => StoredProcedures.ApplyRenewalAsync(_db, orderId, days, operatorId),
            _logger, "订单续租", reset: () => _db.ChangeTracker.Clear());
}

using ERental.Data;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class LogService(AppDbContext db)
{
    private readonly AppDbContext _db = db;

    /// <summary>
    /// 日志分页查询（需求 4.2.1「日志管理」：支持按用户、时间、操作类型筛选查询）。
    /// 时间按「自然日」闭区间处理：to 当天 23:59:59 也要包含，故上界取次日零点做开区间比较。
    /// </summary>
    public async Task<(int Total, List<SystemLog> Items)> GetAll(
        int page = 1, int size = 20, int? userId = null,
        DateTime? from = null, DateTime? to = null,
        string? module = null, string? result = null, string? keyword = null)
    {
        var q = _db.SystemLogs.AsQueryable();
        if (userId.HasValue) q = q.Where(l => l.UserId == userId);
        if (from.HasValue) q = q.Where(l => l.OperateTime >= from.Value);
        var endExclusive = to?.Date.AddDays(1);
        if (endExclusive.HasValue) q = q.Where(l => l.OperateTime < endExclusive.Value);
        if (!string.IsNullOrWhiteSpace(module)) q = q.Where(l => l.OperateModule == module);
        if (!string.IsNullOrWhiteSpace(result)) q = q.Where(l => l.OperateResult == result);
        if (!string.IsNullOrWhiteSpace(keyword)) q = q.Where(l => l.OperateContent != null && l.OperateContent.Contains(keyword));

        var total = await q.CountAsync();
        var items = await q.OrderByDescending(l => l.OperateTime)
            .Skip((page - 1) * size).Take(size).ToListAsync();
        return (total, items);
    }

    /// <summary>日志中出现过的操作模块（供筛选下拉取值，不硬编码模块名）</summary>
    public async Task<List<string>> GetModules() =>
        await _db.SystemLogs
            .Where(l => l.OperateModule != null)
            .Select(l => l.OperateModule!)
            .Distinct().OrderBy(m => m).ToListAsync();

    public async Task WriteLog(int? userId, string content, string module, string result, string? ip = null)
    {
        _db.SystemLogs.Add(new SystemLog
        {
            UserId = userId,
            OperateContent = content,
            OperateModule = module,
            OperateResult = result,
            IpAddress = ip,
            OperateTime = DateTime.Now
        });
        await _db.SaveChangesAsync();
    }
}

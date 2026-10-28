using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class LogService(AppDbContext db)
{
    private readonly AppDbContext _db = db;

    /// <summary>
    /// 日志分页查询（需求 4.2.1「日志管理」：支持按用户、时间、操作类型筛选查询）。
    /// 时间按「自然日」闭区间处理：to 当天 23:59:59 也要包含，故上界取次日零点做开区间比较。
    ///
    /// 返回前会把 userId 解成「谁」：先查工作人员表，再查顾客档案
    /// （顾客的自助动作也会写日志），两处都查不到说明账号已被删除，标记为 unknown。
    /// </summary>
    public async Task<(int Total, List<SystemLogDto> Items)> GetAll(
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
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            // 关键字同时匹配「操作内容」与「操作人（姓名/账号）」：
            // 把原来只能填数字用户 ID 的操作人筛选，升级成一次姓名/账号搜索即可定位到某个人，
            // 管理员无需再记住 16 位内部 ID（这也是「操作人而不是操作 ID」的落点）。
            var staffIds = _db.SystemUsers
                .Where(u => (u.UserName != null && u.UserName.Contains(kw))
                         || (u.LoginAccount != null && u.LoginAccount.Contains(kw)))
                .Select(u => u.UserId);
            var customerIds = _db.Customers
                .Where(c => (c.FullName != null && c.FullName.Contains(kw))
                         || (c.LoginAccount != null && c.LoginAccount.Contains(kw)))
                .Select(c => c.CustomerId);
            q = q.Where(l =>
                (l.OperateContent != null && l.OperateContent.Contains(kw))
                || (l.UserId != null && (staffIds.Contains(l.UserId.Value) || customerIds.Contains(l.UserId.Value))));
        }

        var total = await q.CountAsync();
        var rows = await q.OrderByDescending(l => l.OperateTime)
            .Skip((page - 1) * size).Take(size).ToListAsync();

        // 只为本页涉及的 userId 做两次批量查询，避免每行一次往返
        var ids = rows.Where(r => r.UserId.HasValue).Select(r => r.UserId!.Value).Distinct().ToList();
        var staff = ids.Count == 0
            ? new Dictionary<int, (string? UserName, string LoginAccount)>()
            : await _db.SystemUsers.AsNoTracking()
                .Where(u => ids.Contains(u.UserId))
                .Select(u => new { u.UserId, u.UserName, u.LoginAccount })
                .ToDictionaryAsync(u => u.UserId, u => (u.UserName, u.LoginAccount));
        var customers = ids.Count == 0
            ? new Dictionary<int, (string FullName, string? LoginAccount)>()
            : await _db.Customers.AsNoTracking()
                .Where(c => ids.Contains(c.CustomerId))
                .Select(c => new { c.CustomerId, c.FullName, c.LoginAccount })
                .ToDictionaryAsync(c => c.CustomerId, c => (c.FullName, c.LoginAccount));

        var items = rows.Select(l =>
        {
            var name = string.Empty;
            var account = string.Empty;
            var type = "system";
            if (l.UserId is int id)
            {
                if (staff.TryGetValue(id, out var s))
                {
                    name = s.UserName ?? string.Empty;
                    account = s.LoginAccount ?? string.Empty;
                    type = "staff";
                }
                else if (customers.TryGetValue(id, out var c))
                {
                    name = c.FullName ?? string.Empty;
                    account = c.LoginAccount ?? string.Empty;
                    type = "customer";
                }
                else
                {
                    type = "unknown";   // 账号已删除，日志仍保留（合规要求：日志不可删）
                }
            }

            return new SystemLogDto
            {
                LogId = l.LogId,
                UserId = l.UserId,
                OperatorName = name,
                OperatorAccount = account,
                OperatorType = type,
                OperateModule = l.OperateModule,
                OperateContent = l.OperateContent,
                OperateResult = l.OperateResult,
                IpAddress = l.IpAddress,
                OperateTime = l.OperateTime
            };
        }).ToList();

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

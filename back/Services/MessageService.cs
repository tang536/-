using ERental.Data;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>
/// 通用消息服务：所有角色（顾客 / 工作人员）的通知统一落 message 表。
///
/// 顾客侧——由业务事件触发写入（结算退款去向 / 线上补缴单 / 补缴成功回执）；
/// 员工侧——由 <see cref="ReportService"/> 在查询通知中心时把实时预警同步落库。
/// 已读状态直接更新消息行（is_read / read_at），不额外维护已读表。
///
/// 幂等键：(receiver_type, receiver_id, ref_type, ref_id)，同一条业务通知对每个接收人仅一行。
/// </summary>
public class MessageService(AppDbContext db, ILogger<MessageService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly ILogger<MessageService> _logger = logger;

    /// <summary>
    /// 推送 / 更新一条消息（幂等 upsert）。
    /// 重复触发只会刷新标题/正文/级别/跳转并重置为未读，不会刷出多条。
    /// </summary>
    public async Task SendAsync(string receiverType, int receiverId, string refType, string? refId,
        string title, string content, string? level = null, string? link = null)
    {
        try
        {
            await _db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO `message`
                    (`receiver_type`, `receiver_id`, `ref_type`, `ref_id`, `title`, `content`, `level`, `link`, `is_read`, `created_at`)
                VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, 0, NOW())
                ON DUPLICATE KEY UPDATE
                    `title` = VALUES(`title`),
                    `content` = VALUES(`content`),
                    `level` = VALUES(`level`),
                    `link` = VALUES(`link`),
                    `is_read` = 0,
                    `read_at` = NULL,
                    `created_at` = NOW();
                """,
                receiverType, receiverId, refType, (object?)refId ?? DBNull.Value,
                title, content, (object?)level ?? DBNull.Value, (object?)link ?? DBNull.Value);
        }
        catch (Exception ex)
        {
            // 通知是「尽力而为」的旁路：主链路不能因通知写入失败而回滚
            _logger.LogWarning(ex, "推送消息失败（{ReceiverType}/{ReceiverId}/{RefType}/{RefId}）",
                receiverType, receiverId, refType, refId);
        }
    }

    /// <summary>查询某接收人的消息（按时间倒序），并附未读计数</summary>
    public async Task<object> ListAsync(string receiverType, int receiverId)
    {
        var items = await _db.Messages
            .Where(m => m.ReceiverType == receiverType && m.ReceiverId == receiverId)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new
            {
                m.MessageId,
                m.RefType,
                m.RefId,
                m.Title,
                m.Content,
                m.Level,
                m.Link,
                m.IsRead,
                m.CreatedAt,
                m.ReadAt
            })
            .ToListAsync();

        var unread = items.Count(i => !i.IsRead);
        return new { Items = items, Unread = unread };
    }

    /// <summary>标记已读：ids 为空表示「全部标记已读」，只影响该接收人自己的消息</summary>
    public async Task MarkReadAsync(string receiverType, int receiverId, List<int>? ids)
    {
        var q = _db.Messages.Where(m => m.ReceiverType == receiverType
                                       && m.ReceiverId == receiverId
                                       && !m.IsRead);
        if (ids != null && ids.Count > 0)
            q = q.Where(m => ids.Contains(m.MessageId));

        var now = DateTime.Now;
        var targets = await q.ToListAsync();
        foreach (var m in targets)
        {
            m.IsRead = true;
            m.ReadAt = now;
        }
        if (targets.Count > 0) await _db.SaveChangesAsync();
    }

    /// <summary>
    /// 删除消息：ids 为空表示「清空该接收人的全部消息」。
    /// 删除范围按 (receiverType, receiverId) 限定归属，前端传参无法越权删除他人消息。
    /// 返回实际删除条数。
    /// </summary>
    public async Task<int> DeleteAsync(string receiverType, int receiverId, List<int>? ids)
    {
        var q = _db.Messages.Where(m => m.ReceiverType == receiverType && m.ReceiverId == receiverId);
        if (ids != null && ids.Count > 0)
            q = q.Where(m => ids.Contains(m.MessageId));

        var targets = await q.ToListAsync();
        if (targets.Count == 0) return 0;

        _db.Messages.RemoveRange(targets);
        await _db.SaveChangesAsync();
        return targets.Count;
    }
}

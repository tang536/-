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
    /// 重复触发只会刷新标题/正文/级别/跳转；<b>内容没变就不动已读状态与时间</b>——
    /// 否则轮询每 60 秒就把用户刚读过的预警重新标成未读，未读角标永远清不掉。
    ///
    /// <paramref name="keepReadStateOnChange"/> 专供「文案会随时间自己走」的实时预警：
    /// 逾期提醒的标题是「已逾期 57 分钟 / 3 小时 / 1 天 2 小时」，每过一小时就会变一次，
    /// 按「内容变了就清已读」的规则会让员工刚点掉的红点每小时又亮起来。
    /// 传 true 时只刷新文案，已读状态与首见时间保持不动。
    /// </summary>
    public async Task SendAsync(string receiverType, int receiverId, string refType, string? refId,
        string title, string content, string? level = null, string? link = null,
        bool keepReadStateOnChange = false)
    {
        try
        {
            /*
             * 参数一律以「字符串」形式传入，空值用空串 + SQL 侧 NULLIF 还原成 NULL。
             *
             * 原因：EF 的 ExecuteSqlRaw 会按参数的实际 CLR 类型去找类型映射，
             * 传 DBNull.Value 时它拿 System.DBNull 去查映射表，直接抛
             * "No mapping found for type 'System.DBNull'"——通知就被 catch 悄悄吞掉了
             * （表现：催缴/补缴接口返回成功，顾客端却永远收不到消息）。
             */
            if (keepReadStateOnChange)
            {
                await _db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO `message`
                        (`receiver_type`, `receiver_id`, `ref_type`, `ref_id`, `title`, `content`, `level`, `link`, `is_read`, `created_at`)
                    VALUES ({0}, {1}, {2}, NULLIF({3}, ''), {4}, {5}, NULLIF({6}, ''), NULLIF({7}, ''), 0, NOW())
                    ON DUPLICATE KEY UPDATE
                        `title`   = VALUES(`title`),
                        `content` = VALUES(`content`),
                        `level`   = VALUES(`level`),
                        `link`    = VALUES(`link`);
                    """,
                    receiverType, receiverId, refType, refId ?? string.Empty,
                    title, content, level ?? string.Empty, link ?? string.Empty);
                return;
            }

            await _db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO `message`
                    (`receiver_type`, `receiver_id`, `ref_type`, `ref_id`, `title`, `content`, `level`, `link`, `is_read`, `created_at`)
                VALUES ({0}, {1}, {2}, NULLIF({3}, ''), {4}, {5}, NULLIF({6}, ''), NULLIF({7}, ''), 0, NOW())
                ON DUPLICATE KEY UPDATE
                    `is_read`    = IF(`title` <> VALUES(`title`) OR `content` <> VALUES(`content`), 0, `is_read`),
                    `read_at`    = IF(`title` <> VALUES(`title`) OR `content` <> VALUES(`content`), NULL, `read_at`),
                    `created_at` = IF(`title` <> VALUES(`title`) OR `content` <> VALUES(`content`), NOW(), `created_at`),
                    `title`      = VALUES(`title`),
                    `content`    = VALUES(`content`),
                    `level`      = VALUES(`level`),
                    `link`       = VALUES(`link`);
                """,
                receiverType, receiverId, refType, refId ?? string.Empty,
                title, content, level ?? string.Empty, link ?? string.Empty);
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
            m.MarkRead(now);
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

    /// <summary>
    /// 忽略（软删）消息：ids 为空表示「忽略该接收人的全部消息」。返回实际忽略条数。
    ///
    /// 与 <see cref="DeleteAsync"/> 的分工：顾客消息是真实业务事件、不会重建，硬删即可；
    /// 员工预警由实时状态推导（ReportService 每次查询都幂等 upsert），硬删会被下一次
    /// 拉取复活，用户会以为「删除没用」。软删后读取端过滤 dismissed_at，预警消失时
    /// 行被硬删、标记一并清除，之后再次触发按新预警处理——「忽略」只在预警存续期间有效。
    /// 范围同样按 (receiverType, receiverId) 限定归属。
    /// </summary>
    public async Task<int> DismissAsync(string receiverType, int receiverId, List<int>? ids)
    {
        var q = _db.Messages.Where(m => m.ReceiverType == receiverType
                                       && m.ReceiverId == receiverId
                                       && m.DismissedAt == null);
        if (ids != null && ids.Count > 0)
            q = q.Where(m => ids.Contains(m.MessageId));

        var now = DateTime.Now;
        var targets = await q.ToListAsync();
        foreach (var m in targets) m.Dismiss(now);
        if (targets.Count > 0) await _db.SaveChangesAsync();
        return targets.Count;
    }
}

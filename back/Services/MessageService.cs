using ERental.Data;
using ERental.DTOs;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>通用消息服务：所有角色（顾客 / 工作人员）的通知统一落 message 表、统一从这里读写</summary>
public class MessageService(AppDbContext db, ILogger<MessageService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly ILogger<MessageService> _logger = logger;

    /// <summary>推送 / 更新一条消息（幂等 upsert）</summary>
    public async Task SendAsync(string receiverType, int receiverId, string refType, string? refId,
        string title, string content, string? level = null, string? link = null,
        bool keepReadStateOnChange = false)
    {
        // 正文列是 VARCHAR(500)（见 AppMessage.Content 的注释）。超长内容会被 MySQL 以 1406
        // 拒绝（严格模式）或静默截断（非严格模式），两种都比「先裁剪」更糟：
        // 通知是旁路，不该让一条过长的预警文案把整笔业务动作带崩。
        title = Clip(title, 120);
        content = Clip(content, 500);

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

    /// <summary>按列宽截断字符串</summary>
    private static string Clip(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= max) return text;
        var cut = max;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut];
    }

    /// <summary>单次返回的消息条数上限</summary>
    public const int MaxPageSize = 200;

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>查询某接收人的消息（按时间倒序分页），并附未读计数</summary>
    public async Task<NotificationListDto> ListAsync(string receiverType, int receiverId,
        int page = 1, int pageSize = DefaultPageSize)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        // AsNoTracking：列表只读，不需要变更跟踪器，省掉每行一份的快照；
        // 已忽略（软删）的行不返回。
        var scope = _db.Messages.AsNoTracking()
            .Where(m => m.ReceiverType == receiverType && m.ReceiverId == receiverId
                        && m.DismissedAt == null);

        var rows = await scope
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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

        var unread = await scope.CountAsync(m => !m.IsRead);
        var total = await scope.CountAsync();

        return new NotificationListDto
        {
            // Category 由本地方法推导，进不了 SQL，所以先投影列、再在内存里组装 DTO
            Items = rows.Select(m => new NotificationDto
            {
                MessageId = m.MessageId,
                Id = string.IsNullOrEmpty(m.RefId) ? m.RefType : $"{m.RefType}-{m.RefId}",
                Level = m.Level ?? "info",
                Category = NotificationCategories.Of(m.RefType),
                Title = m.Title,
                Description = m.Content,
                Time = m.CreatedAt,
                Link = m.Link,
                IsRead = m.IsRead,
                ReadTime = m.ReadAt
            }).ToList(),
            Unread = unread,
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>标记已读：ids 为空表示「全部标记已读」，只影响该接收人自己的消息</summary>
    public async Task MarkReadAsync(string receiverType, int receiverId, List<int>? ids)
    {
        var q = _db.Messages.Where(m => m.ReceiverType == receiverType
                                       && m.ReceiverId == receiverId
                                       && !m.IsRead);
        if (ids != null && ids.Count > 0)
            q = q.Where(m => ids.Contains(m.MessageId));

        // 一条 UPDATE ... WHERE 取代「查出实体 → 逐行赋值 → SaveChanges」：
        // 后者会把每行的正文（content）一并读进内存再按行发 UPDATE，「全部已读」时
        // 往返次数与消息条数成正比。两列必须由同一条语句写入，与 MarkRead 的语义一致
        // （否则会出现「已读但没有阅读时间」）。
        var now = DateTime.Now;
        await q.ExecuteUpdateAsync(s => s
            .SetProperty(m => m.IsRead, true)
            .SetProperty(m => m.ReadAt, now));
    }

    /// <summary>删除（忽略）消息：ids 为空表示「清空该接收人的全部消息」</summary>
    public async Task<int> DismissAsync(string receiverType, int receiverId, List<int>? ids)
    {
        var q = _db.Messages.Where(m => m.ReceiverType == receiverType
                                       && m.ReceiverId == receiverId
                                       && m.DismissedAt == null);
        if (ids != null && ids.Count > 0)
            q = q.Where(m => ids.Contains(m.MessageId));

        var now = DateTime.Now;
        // 同样是单条 UPDATE：受影响行数即「实际忽略条数」，
        // 过滤条件里的 dismissed_at IS NULL 保证重复忽略不会被计数两次。
        return await q.ExecuteUpdateAsync(s => s.SetProperty(m => m.DismissedAt, now));
    }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

/// <summary>
/// 通知已读记录。
///
/// 背景：顶栏「通知与预警」的内容是**实时算出来的**（逾期 / 24h 内到期 / 待支付 /
/// 维保中 / 资料待完善 / 保养到期），并不存在业务表里，因此没有天然的主键可用来记「已读」。
/// 这里用一张小表按「用户 + 通知条目 ID」记录已读事实：
///   · NotificationId 是业务侧生成的**稳定字符串**（overdue-12、expiring-8、maintenance …），
///     同一笔业务的同一条提醒每次生成都相同，因此已读状态可以跨请求、跨登录保持；
///   · user_id 为空索引列之外的普通列——通知只对工作人员（管理员/前台/财务）开放。
/// </summary>
[Table("notification_read")]
public class NotificationRead
{
    [Key]
    [Column("read_id")]
    public int ReadId { get; init; }

    /// <summary>已读人（system_user.user_id）</summary>
    [Column("user_id")]
    public int UserId { get; init; }

    /// <summary>通知条目 ID（业务生成，稳定可复现）</summary>
    [Required, MaxLength(60)]
    [Column("notification_id")]
    public string NotificationId { get; init; } = string.Empty;

    [Column("read_time")]
    public DateTime ReadTime { get; init; } = DateTime.Now;
}

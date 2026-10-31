using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

/// <summary>通用消息表（统一通知中心），顾客事件通知与工作人员预警消息共用一张表</summary>
[Table("message")]
public class AppMessage
{
    [Key]
    [Column("message_id")]
    public int MessageId { get; init; }

    /// <summary>接收人类型：customer / staff</summary>
    [Required, MaxLength(10)]
    [Column("receiver_type")]
    public string ReceiverType { get; init; } = string.Empty;

    /// <summary>接收人 ID：customer 时为 customer_id</summary>
    [Column("receiver_id")]
    public int ReceiverId { get; init; }

    /// <summary>业务类型（稳定可复现，便于按场景筛选与统计）： 顾客侧 —— refund=结算退款去向 / supplement=…</summary>
    [Required, MaxLength(20)]
    [Column("ref_type")]
    public string RefType { get; init; } = string.Empty;

    /// <summary>业务实体 ID（如订单号/车辆ID），用于幂等去重与点击跳转定位</summary>
    [MaxLength(60)]
    [Column("ref_id")]
    public string? RefId { get; init; }

    [MaxLength(120)]
    [Column("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>消息正文</summary>
    [MaxLength(500)]
    [Column("content")]
    public string Content { get; init; } = string.Empty;

    /// <summary>预警级别（员工侧预警用）：danger / warning / info / success</summary>
    [MaxLength(10)]
    [Column("level")]
    public string? Level { get; init; }

    /// <summary>点击后跳转的前端路由（如 /orders、/my-orders）</summary>
    [MaxLength(120)]
    [Column("link")]
    public string? Link { get; init; }

    /// <summary>是否已读（0=未读，1=已读）</summary>
    [Column("is_read")]
    public bool IsRead { get; private set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    [Column("read_at")]
    public DateTime? ReadAt { get; private set; }

    /// <summary>忽略时间（员工侧「删除」通知 = 软删）：非空即对该接收人隐藏</summary>
    [Column("dismissed_at")]
    public DateTime? DismissedAt { get; private set; }

    // -------------------------------------------------------------- 领域方法
    /// <summary>标记已读（已读时间与「已读」标志必须同进同出，否则会出现「已读但没有阅读时间」）</summary>
    public void MarkRead(DateTime readAt)
    {
        IsRead = true;
        ReadAt = readAt;
    }

    /// <summary>软删（员工侧「删除通知」）：只写忽略时间，行保留</summary>
    public void Dismiss(DateTime dismissedAt) => DismissedAt = dismissedAt;
}

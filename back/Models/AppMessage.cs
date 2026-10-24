using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

/// <summary>
/// 通用消息表（统一通知中心），顾客事件通知与工作人员预警消息共用一张表。
///
/// 任何角色的通知都落同一张表，并按
/// (receiver_type, receiver_id, ref_type, ref_id) 幂等：
/// 同一条业务通知对每个接收人只保留一行，重复触发只会刷新内容并重置为未读，
/// 不会刷出多条历史噪音。
///
/// receiver_type 取值：
///   · 'customer' —— 接收人为租车顾客（receiver_id = customer.customer_id）；
///   · 'staff'    —— 接收人为工作人员（receiver_id = system_user.user_id）。
/// </summary>
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

    /// <summary>接收人 ID：customer 时为 customer_id；staff 时为 system_user.user_id</summary>
    [Column("receiver_id")]
    public int ReceiverId { get; init; }

    /// <summary>
    /// 业务类型（稳定可复现，便于按场景筛选与统计）：
    /// 顾客侧 —— refund=结算退款去向 / supplement=逾期线上补缴单 / supplement_paid=补缴成功回执；
    /// 员工侧 —— overdue=逾期预警 / expiring=即将到期 / pending=待支付 / maintenance=维保中 /
    ///           profile_incomplete=资料待完善 / maintain_due=保养到期。
    /// </summary>
    [Required, MaxLength(20)]
    [Column("ref_type")]
    public string RefType { get; init; } = string.Empty;

    /// <summary>业务实体 ID（如订单号/车辆ID），用于幂等去重与点击跳转定位；全员类预警可为空</summary>
    [MaxLength(60)]
    [Column("ref_id")]
    public string? RefId { get; init; }

    [MaxLength(120)]
    [Column("title")]
    public string Title { get; init; } = string.Empty;

    [Column("content", TypeName = "TEXT")]
    public string Content { get; init; } = string.Empty;

    /// <summary>预警级别（员工侧预警用）：danger / warning / info / success；顾客事件消息可为空</summary>
    [MaxLength(10)]
    [Column("level")]
    public string? Level { get; init; }

    /// <summary>点击后跳转的前端路由（如 /orders、/my-orders）</summary>
    [MaxLength(120)]
    [Column("link")]
    public string? Link { get; init; }

    /// <summary>是否已读（0=未读，1=已读）</summary>
    [Column("is_read")]
    public bool IsRead { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    [Column("read_at")]
    public DateTime? ReadAt { get; set; }
}

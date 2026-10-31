using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace ERental.Models;

[Table("rental_order")]
public class RentalOrder
{
    [Key]
    [Column("order_id")]
    public int OrderId { get; init; }

    [Required, MaxLength(30)]
    [Column("order_no")]
    public string OrderNo { get; init; } = string.Empty;

    [Column("customer_id")]
    public int CustomerId { get; init; }

    [ForeignKey("CustomerId")]
    public Customer? Customer { get; init; }

    [Column("vehicle_id")]
    public int VehicleId { get; init; }

    [ForeignKey("VehicleId")]
    public Vehicle? Vehicle { get; init; }

    [Column("lease_start")]
    public DateTime LeaseStart { get; init; }

    [Column("lease_end")]
    public DateTime LeaseEnd { get; private set; }

    [Column("actual_return")]
    public DateTime? ActualReturn { get; private set; }

    [Column("total_rent", TypeName = "decimal(10,2)")]
    public decimal TotalRent { get; init; }

    [Column("deposit", TypeName = "decimal(10,2)")]
    public decimal Deposit { get; init; }

    /// <summary>订单状态</summary>
    [ConcurrencyCheck]
    [MaxLength(10)]
    [Column("order_status")]
    public string OrderStatus { get; private set; } = "待支付";

    [Column("create_time")]
    public DateTime CreateTime { get; init; } = DateTime.Now;

    [MaxLength(200)]
    [Column("cancel_reason")]
    public string? CancelReason { get; private set; }

    /// <summary>提前还车申请状态：null = 未申请</summary>
    [MaxLength(20)]
    [Column("early_return_status")]
    public string? EarlyReturnStatus { get; private set; }

    [Column("operator_id")]
    public int? OperatorId { get; init; }

    // 支付/结算流水只给 EF 用，不进 JSON：订单详情接口若把它们序列化出去，
    // 一是账务明细随订单无条件外泄，二是 PaymentRecord.RentalOrder / SettleRecord.RentalOrder
    // 指回本对象会成环（System.Text.Json 抛 "A possible object cycle was detected"）。
    [JsonIgnore] public ICollection<PaymentRecord> PaymentRecords { get; init; } = new List<PaymentRecord>();
    [JsonIgnore] public ICollection<SettleRecord> SettleRecords { get; init; } = new List<SettleRecord>();

    // ------------------------------------------------------------------ 工厂
    /// <summary>开单（用况2 / 用况6）：订单号、客户、车辆、租期与金额在创建时一次定死， 之后只有「续租」能延长到期时间、只有「…</summary>
    public static RentalOrder Create(string orderNo, int customerId, int vehicleId, DateTime leaseStart, DateTime leaseEnd, decimal totalRent, decimal deposit, int? operatorId, string orderStatus = "待支付") =>
        new()
        {
            OrderNo = orderNo,
            CustomerId = customerId,
            VehicleId = vehicleId,
            LeaseStart = leaseStart,
            LeaseEnd = leaseEnd,
            TotalRent = totalRent,
            Deposit = deposit,
            OperatorId = operatorId,
            OrderStatus = orderStatus,
        };

    // -------------------------------------------------------------- 领域方法
    /// <summary>状态迁移（待支付 → 已支付 → 租赁中 → 已完结 / 已取消）</summary>
    public void ChangeStatus(string status) => OrderStatus = status;

    /// <summary>取消订单：置「已取消」并留下原因，便于对账时区分「顾客取消」「超时回收」「支付失败」</summary>
    public void Cancel(string? reason)
    {
        OrderStatus = "已取消";
        CancelReason = string.IsNullOrWhiteSpace(reason) ? CancelReason : reason.Trim();
    }

    /// <summary>续租：只顺延到期时间，租金增量已由 RenewalRecord 单独留存，不改本单原始金额</summary>
    public void ExtendLease(DateTime newLeaseEnd) => LeaseEnd = newLeaseEnd;

    /// <summary>还车结算完成：写实际归还时间并把订单置为「已完结」（与结算单在同一事务内落库）</summary>
    public void MarkReturned(DateTime actualReturn)
    {
        ActualReturn = actualReturn;
        OrderStatus = "已完结";
    }

    /// <summary>顾客提交提前还车申请：未申请（null）→ 待审核</summary>
    public void RequestEarlyReturn() => EarlyReturnStatus = "待审核";

    /// <summary>前台审核提前还车：通过写空串（已处理，按实际用车时长结算）， 驳回则回置为 null（订单按原租期继续计费）并记录驳…</summary>
    public void ReviewEarlyReturn(bool approve, string? rejectReason = null)
    {
        EarlyReturnStatus = approve ? string.Empty : null;
        if (!approve && !string.IsNullOrWhiteSpace(rejectReason)) CancelReason = rejectReason.Trim();
    }

    /// <summary>结算时清掉已处理的提前还车标记（避免同一订单二次进入审核队列）</summary>
    public void ClearEarlyReturnFlag() => EarlyReturnStatus = string.Empty;
}

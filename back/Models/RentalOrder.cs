using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using ERental.Services;

namespace ERental.Models;

[Table("rental_order")]
public class RentalOrder
{
    ///<summary>订单号长度上限</summary>
    private const int OrderNoMaxLength = 30;

    ///<summary>订单状态机白名单：待支付</summary>
    private static readonly string[] LegalStatuses = ["待支付", "已支付", "租赁中", "已完结", "已取消"];

    ///<summary>状态值是否在白名单内</summary>
    private static bool IsValidStatus(string? value) => value is not null && Array.IndexOf(LegalStatuses, value) >= 0;

    [Key]
    [Column("order_id")]
    public int OrderId { get; init; }

    [Required, MaxLength(30)]
    [Column("order_no")]
    public string OrderNo { get; private set; } = string.Empty;

    [Column("customer_id")]
    public int CustomerId { get; private set; }

    [ForeignKey("CustomerId")]
    public Customer? Customer { get; init; }

    [Column("vehicle_id")]
    public int VehicleId { get; private set; }

    [ForeignKey("VehicleId")]
    public Vehicle? Vehicle { get; init; }

    [Column("lease_start")]
    public DateTime LeaseStart { get; private set; }

    [Column("lease_end")]
    public DateTime LeaseEnd { get; private set; }

    [Column("actual_return")]
    public DateTime? ActualReturn { get; private set; }

    [Column("total_rent", TypeName = "decimal(10,2)")]
    public decimal TotalRent { get; private set; }

    [Column("deposit", TypeName = "decimal(10,2)")]
    public decimal Deposit { get; private set; }

    ///<summary>订单状态</summary>
    [ConcurrencyCheck]
    [MaxLength(10)]
    [Column("order_status")]
    public string OrderStatus { get; private set; } = "待支付";

    [Column("create_time")]
    public DateTime CreateTime { get; init; } = DateTime.Now;

    [MaxLength(200)]
    [Column("cancel_reason")]
    public string? CancelReason { get; private set; }

    ///<summary>提前还车申请状态：null=未申请</summary>
    [MaxLength(20)]
    [Column("early_return_status")]
    public string? EarlyReturnStatus { get; private set; }

    [Column("operator_id")]
    public int? OperatorId { get; private set; }

    //支付/结算流水只给 EF 用
    private readonly List<PaymentRecord> _paymentRecords = [];
    [JsonIgnore] public IReadOnlyCollection<PaymentRecord> PaymentRecords => _paymentRecords;
    private readonly List<SettleRecord> _settleRecords = [];
    [JsonIgnore] public IReadOnlyCollection<SettleRecord> SettleRecords => _settleRecords;
    ///<summary>开单：订单号、客户、车辆、租期与金额在创建时一次定死</summary>
    public static RentalOrder Create(
        (string OrderNo, int CustomerId, int VehicleId, int? OperatorId) basic,
        (DateTime LeaseStart, DateTime LeaseEnd) lease,
        (decimal TotalRent, decimal Deposit) pricing,
        string orderStatus = "待支付")
    {
        var (orderNo, customerId, vehicleId, operatorId) = basic;
        var (leaseStart, leaseEnd) = lease;
        var (totalRent, deposit) = pricing;

        if (string.IsNullOrWhiteSpace(orderNo)) throw new BusinessException("订单号不能为空");
        if (orderNo.Trim().Length > OrderNoMaxLength)
            throw new BusinessException($"订单号不能超过 {OrderNoMaxLength} 位");
        if (customerId <= 0 || vehicleId <= 0) throw new BusinessException("订单必须关联有效的顾客与车辆");
        if (leaseEnd < leaseStart) throw new BusinessException("租期结束时间不能早于开始时间");
        if (totalRent < 0 || deposit < 0) throw new BusinessException("租金与押金不能为负数");
        if (!IsValidStatus(orderStatus)) throw new BusinessException("订单状态值非法：" + orderStatus);

        return new RentalOrder
        {
            OrderNo = orderNo.Trim(),
            CustomerId = customerId,
            VehicleId = vehicleId,
            LeaseStart = leaseStart,
            LeaseEnd = leaseEnd,
            TotalRent = totalRent,
            Deposit = deposit,
            OperatorId = operatorId,
            OrderStatus = orderStatus,
        };
    }

    ///<summary>状态迁移（待支付 → 已支付 → 租赁中 → 已完结/已取消），只接受白名单内的状态值</summary>
    public void ChangeStatus(string status)
    {
        if (!IsValidStatus(status)) throw new BusinessException("订单状态值非法：" + status);
        OrderStatus = status;
    }

    ///<summary>取消订单：置「已取消」并留下原因，便于对账时区分「顾客取消」「超时回收」「支付失败」</summary>
    public void Cancel(string? reason)
    {
        OrderStatus = "已取消";
        CancelReason = string.IsNullOrWhiteSpace(reason) ? CancelReason : reason.Trim();
    }

    ///<summary>还车结算完成：写实际归还时间并把订单置为「已完结」（与结算单在同一事务内落库）</summary>
    public void MarkReturned(DateTime actualReturn)
    {
        ActualReturn = actualReturn;
        OrderStatus = "已完结";
    }

    ///<summary>顾客提交提前还车申请：未申请（null）→ 待审核</summary>
    public void RequestEarlyReturn() => EarlyReturnStatus = "待审核";

    ///<summary>前台审核提前还车：通过写空串（已处理，按实际用车时长结算）</summary>
    public void ReviewEarlyReturn(bool approve, string? rejectReason = null)
    {
        EarlyReturnStatus = approve ? string.Empty : null;
        if (!approve && !string.IsNullOrWhiteSpace(rejectReason)) CancelReason = rejectReason.Trim();
    }

    ///<summary>结算时清掉已处理的提前还车标记</summary>
    public void ClearEarlyReturnFlag() => EarlyReturnStatus = string.Empty;
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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
    public DateTime LeaseEnd { get; set; }

    [Column("actual_return")]
    public DateTime? ActualReturn { get; set; }

    [Column("total_rent", TypeName = "decimal(10,2)")]
    public decimal TotalRent { get; init; }

    [Column("deposit", TypeName = "decimal(10,2)")]
    public decimal Deposit { get; init; }

    /// <summary>
    /// 订单状态。**带乐观并发令牌**（理由同 <see cref="Vehicle.VehicleStatus"/>）。
    ///
    /// 订单状态的写入有两条路径：锁内改（<c>DbRowLock.LockOrderAsync</c> 重读后写，安全）
    /// 与裸 SQL / 另一实例的写入。没有并发令牌时，EF 的
    /// <c>UPDATE rental_order SET order_status=@new WHERE order_id=@id</c>
    /// 会把「读」与「写」之间别人做的状态迁移**静默覆盖**——
    /// 典型是超时回收把「已支付」的单改成「已取消」，钱收了单没了。
    /// 加上之后 EF 会比对原值，冲突就抛 <c>DbUpdateConcurrencyException</c>，由调用方重读重判。
    /// </summary>
    [ConcurrencyCheck]
    [MaxLength(10)]
    [Column("order_status")]
    public string OrderStatus { get; set; } = "待支付";

    [Column("create_time")]
    public DateTime CreateTime { get; init; } = DateTime.Now;

    [MaxLength(200)]
    [Column("cancel_reason")]
    public string? CancelReason { get; set; }

    /// <summary>
    /// 提前还车申请状态：null = 未申请；"待审核" = 顾客自助申请、等前台处理；
    /// ""（空串）= 已处理完（通过或驳回）。
    /// 顾客自助申请 + 前台审核（需求：用户可自主提前还车，前台负责审核），
    /// 审核通过按实际用车时长多退少补结算。不建新表，避免破坏图4-x 的表结构对应关系。
    /// </summary>
    [MaxLength(20)]
    [Column("early_return_status")]
    public string? EarlyReturnStatus { get; set; }

    [Column("operator_id")]
    public int? OperatorId { get; init; }

    public ICollection<PaymentRecord> PaymentRecords { get; init; } = new List<PaymentRecord>();
    public ICollection<SettleRecord> SettleRecords { get; init; } = new List<SettleRecord>();
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

[Table("settle_record")]
public class SettleRecord
{
    [Key]
    [Column("settle_id")]
    public int SettleId { get; init; }

    [Column("order_id")]
    public int OrderId { get; init; }

    [ForeignKey("OrderId")]
    public RentalOrder? RentalOrder { get; init; }

    [Column("actual_rent", TypeName = "decimal(10,2)")]
    public decimal ActualRent { get; init; }

    [Column("overdue_fee", TypeName = "decimal(10,2)")]
    public decimal OverdueFee { get; init; }

    [Column("damage_fee", TypeName = "decimal(10,2)")]
    public decimal DamageFee { get; init; }

    [Column("refund_amount", TypeName = "decimal(10,2)")]
    public decimal RefundAmount { get; init; }

    [Column("settle_time")]
    public DateTime SettleTime { get; init; } = DateTime.Now;

    [Column("operator_id")]
    public int? OperatorId { get; init; }

    [Column("supplement_amount", TypeName = "decimal(10,2)")]
    public decimal SupplementAmount { get; init; }

    [MaxLength(200)]
    [Column("remark")]
    public string? Remark { get; init; }
}

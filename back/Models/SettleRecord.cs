using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ERental.Services;
using ERental.Data;

namespace ERental.Models;

[Table("settle_record")]
public class SettleRecord
{
    ///<summary>结算备注长度上限，与 remark 列的[MaxLength(200)] 一致</summary>
    private const int RemarkMaxLength = 200;

    [Key]
    [Column("settle_id")]
    public int SettleId { get; init; }

    [Column("order_id")]
    public int OrderId { get; private set; }

    [ForeignKey("OrderId")]
    public RentalOrder? RentalOrder { get; init; }

    [Column("actual_rent", TypeName = "decimal(10,2)")]
    public decimal ActualRent { get; private set; }

    [Column("overdue_fee", TypeName = "decimal(10,2)")]
    public decimal OverdueFee { get; private set; }

    [Column("damage_fee", TypeName = "decimal(10,2)")]
    public decimal DamageFee { get; private set; }

    [Column("refund_amount", TypeName = "decimal(10,2)")]
    public decimal RefundAmount { get; private set; }

    [Column("settle_time")]
    public DateTime SettleTime { get; private set; } = AppClock.Now;

    [Column("operator_id")]
    public int? OperatorId { get; private set; }

    [Column("supplement_amount", TypeName = "decimal(10,2)")]
    public decimal SupplementAmount { get; private set; }

    [MaxLength(200)]
    [Column("remark")]
    public string? Remark { get; private set; }

    ///<summary>结算落库</summary>
    public static SettleRecord Create(
        (int OrderId, int? OperatorId) basic,
        (decimal ActualRent, decimal OverdueFee, decimal DamageFee) fee,
        (decimal RefundAmount, decimal SupplementAmount, string? Remark) settlement,
        DateTime? settleTime = null)
    {
        var (orderId, operatorId) = basic;
        var (actualRent, overdueFee, damageFee) = fee;
        var (refundAmount, supplementAmount, remark) = settlement;

        if (orderId <= 0) throw new BusinessException("结算单必须关联有效订单");
        if (actualRent < 0 || overdueFee < 0 || damageFee < 0 || refundAmount < 0 || supplementAmount < 0)
            throw new BusinessException("结算金额不能为负数");
        if ((remark?.Length ?? 0) > RemarkMaxLength)
            throw new BusinessException("结算备注不能超过 " + RemarkMaxLength + " 个字符");

        return new SettleRecord
        {
            OrderId = orderId,
            ActualRent = actualRent,
            OverdueFee = overdueFee,
            DamageFee = damageFee,
            RefundAmount = refundAmount,
            SupplementAmount = supplementAmount,
            Remark = string.IsNullOrWhiteSpace(remark) ? null : remark.Trim(),
            OperatorId = operatorId,
            SettleTime = settleTime ?? AppClock.Now,
        };
    }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

[Table("payment_record")]
public class PaymentRecord
{
    [Key]
    [Column("pay_id")]
    public int PayId { get; init; }

    [Column("order_id")]
    public int OrderId { get; init; }

    [ForeignKey("OrderId")]
    public RentalOrder? RentalOrder { get; init; }

    [Column("pay_amount", TypeName = "decimal(10,2)")]
    public decimal PayAmount { get; set; }

    [MaxLength(10)]
    [Column("pay_type")]
    public string? PayType { get; set; }

    [MaxLength(10)]
    [Column("pay_status")]
    public string PayStatus { get; set; } = "待支付";

    [Column("pay_time")]
    public DateTime? PayTime { get; set; }

    [Column("refund_amount", TypeName = "decimal(10,2)")]
    public decimal RefundAmount { get; set; }

    [MaxLength(50)]
    [Column("transaction_no")]
    public string? TransactionNo { get; set; }
}

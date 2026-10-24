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

    /// <summary>
    /// 业务类型：<c>order</c> = 订单首付款（租金+押金）；<c>supplement</c> = 逾期/损坏的线上补缴单。
    /// 同一笔租赁订单可能产生多条支付记录（首付款 + 若干补缴），靠此列区分，互不影响。
    /// 默认 order，存量数据由 Schema 升级补齐。
    /// </summary>
    [MaxLength(10)]
    [Column("biz_type")]
    public string BizType { get; set; } = "order";

    /// <summary>
    /// 支付通道侧的商户订单号（out_trade_no）。
    /// 首付款沿用租赁订单号（<c>rental_order.order_no</c>）作为 out_trade_no；
    /// 补缴单必须拥有**全局唯一**的 out_trade_no（支付宝/微信会拒绝与历史交易重复的订单号），
    /// 故补缴单在此单独存储自己的交易号，格式 <c>SP-{orderNo}-{时间戳}-{随机}</c>。
    /// 为空表示首付款（回退到 order_no 作为 out_trade_no）。
    /// </summary>
    [MaxLength(64)]
    [Column("out_trade_no")]
    public string? OutTradeNo { get; set; }
}

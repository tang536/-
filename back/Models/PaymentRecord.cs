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
    public decimal PayAmount { get; private set; }

    [MaxLength(10)]
    [Column("pay_type")]
    public string? PayType { get; private set; }

    [MaxLength(10)]
    [Column("pay_status")]
    public string PayStatus { get; private set; } = "待支付";

    [Column("pay_time")]
    public DateTime? PayTime { get; private set; }

    [Column("refund_amount", TypeName = "decimal(10,2)")]
    public decimal RefundAmount { get; private set; }

    [MaxLength(50)]
    [Column("transaction_no")]
    public string? TransactionNo { get; private set; }

    /// <summary>
    /// 业务类型：<c>order</c> = 订单首付款（租金+押金）；<c>supplement</c> = 逾期/损坏的线上补缴单。
    /// 同一笔租赁订单可能产生多条支付记录（首付款 + 若干补缴），靠此列区分，互不影响。
    /// 默认 order，存量数据由 Schema 升级补齐。
    /// </summary>
    [MaxLength(10)]
    [Column("biz_type")]
    public string BizType { get; private set; } = "order";

    /// <summary>
    /// 支付通道侧的商户订单号（out_trade_no）。
    /// 首付款沿用租赁订单号（<c>rental_order.order_no</c>）作为 out_trade_no；
    /// 补缴单必须拥有**全局唯一**的 out_trade_no（支付宝/微信会拒绝与历史交易重复的订单号），
    /// 故补缴单在此单独存储自己的交易号，格式 <c>SP-{orderNo}-{时间戳}-{随机}</c>。
    /// 为空表示首付款（回退到 order_no 作为 out_trade_no）。
    /// </summary>
    [MaxLength(64)]
    [Column("out_trade_no")]
    public string? OutTradeNo { get; private set; }

    // ------------------------------------------------------------------ 工厂
    /// <summary>
    /// 新建支付流水（首付款或补缴单）。<paramref name="payStatus"/> 允许直接建「已支付」：
    /// 柜台现金收款是「收款即落账」，不存在先建待支付单再回填的过程。
    /// </summary>
    public static PaymentRecord Create(int orderId, decimal payAmount, string? payType,
        string bizType = "order", string? outTradeNo = null, string payStatus = "待支付") => new()
        {
            OrderId = orderId,
            PayAmount = payAmount,
            PayType = string.IsNullOrWhiteSpace(payType) ? null : payType.Trim(),
            BizType = bizType,
            OutTradeNo = string.IsNullOrWhiteSpace(outTradeNo) ? null : outTradeNo,
            PayStatus = payStatus,
        };

    // -------------------------------------------------------------- 领域方法
    /// <summary>
    /// 收款成功落账：状态、渠道、到账时间、通道交易号一次写齐。
    /// 金额以回调/查询到的实收为准（<paramref name="actualAmount"/> 为空则保持原应付金额）。
    /// </summary>
    public void MarkPaid(string payType, DateTime payTime, string? transactionNo, decimal? actualAmount = null)
    {
        PayStatus = "已支付";
        PayType = string.IsNullOrWhiteSpace(payType) ? PayType : payType.Trim();
        PayTime = payTime;
        TransactionNo = string.IsNullOrWhiteSpace(transactionNo) ? TransactionNo : transactionNo.Trim();
        if (actualAmount.HasValue) PayAmount = actualAmount.Value;
    }

    /// <summary>关闭未支付的流水（订单取消 / 超时回收 / 已由其他渠道收清）。</summary>
    public void Cancel() => PayStatus = "已取消";

    /// <summary>
    /// 选定/切换支付渠道（付款完成前写入顾客意向渠道，员工侧据此看真实意向；
    /// 入账时仍以回调或查单数据最终覆盖）。金额不变，故与 <see cref="PreparePayment"/> 分开。
    /// </summary>
    public void SetChannel(string channel) => PayType = channel.Trim();

    /// <summary>补缴单重新出码：切换支付渠道并按剩余应收重算金额（同一张单可反复换渠道）。</summary>
    public void PreparePayment(string channel, decimal amount)
    {
        PayType = channel.Trim();
        PayAmount = amount;
    }

    /// <summary>登记退款：累计退款金额，并按「是否退满」自动落成「已退款 / 部分退款」。</summary>
    public void ApplyRefund(decimal refundedTotal)
    {
        RefundAmount = refundedTotal;
        PayStatus = refundedTotal >= PayAmount ? "已退款" : "部分退款";
    }
}

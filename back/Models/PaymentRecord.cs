using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ERental.Services;

namespace ERental.Models;

[Table("payment_record")]
public class PaymentRecord
{
    [Key]
    [Column("pay_id")]
    public int PayId{get;init;}

    [Column("order_id")]
    public int OrderId{get;private set;}

    [ForeignKey("OrderId")]
    public RentalOrder? RentalOrder{get;init;}

    [Column("pay_amount",TypeName="decimal(10,2)")]
    public decimal PayAmount{get;private set;}

    [MaxLength(10)]
    [Column("pay_type")]
    public string? PayType{get;private set;}

    [MaxLength(10)]
    [Column("pay_status")]
    public string PayStatus{get;private set;}="待支付";

    [Column("pay_time")]
    public DateTime? PayTime{get;private set;}

    [Column("refund_amount",TypeName="decimal(10,2)")]
    public decimal RefundAmount{get;private set;}

    [MaxLength(50)]
    [Column("transaction_no")]
    public string? TransactionNo{get;private set;}

    ///<summary>业务类型：order=订单首付款（租金+押金）</summary>
    [MaxLength(10)]
    [Column("biz_type")]
    public string BizType{get;private set;}="order";

    ///<summary>支付通道侧的商户订单号（out_trade_no）</summary>
    [MaxLength(64)]
    [Column("out_trade_no")]
    public string? OutTradeNo{get;private set;}

    ///<summary>业务类型：首付款单/补缴单</summary>
    private const string BizTypeOrder="order";
    private const string BizTypeSupplement="supplement";

    ///<summary>支付流水状态白名单</summary>
    private static readonly string[] LegalPayStatuses=["待支付","已支付","部分退款","已退款","待定"];

    ///<summary>新建支付流水（首付款或补缴单）：金额非负、业务类型与状态取白名单，入口一次校验</summary>
    public static PaymentRecord Create(int orderId,decimal payAmount,string? payType,
        string bizType=BizTypeOrder,string? outTradeNo=null,string payStatus="待支付")
    {
        if(orderId<=0) throw new BusinessException("支付流水必须关联有效订单");
        if(payAmount<0) throw new BusinessException("支付金额不能为负数");
        if(bizType is not(BizTypeOrder or BizTypeSupplement))
            throw new BusinessException("支付流水业务类型非法："+bizType);
        if(Array.IndexOf(LegalPayStatuses,payStatus) < 0)
            throw new BusinessException("支付流水状态值非法："+payStatus);
        if((outTradeNo?.Length??0) > 64) throw new BusinessException("商户订单号不能超过 64 个字符");

        return new PaymentRecord
        {
            OrderId=orderId,
            PayAmount=payAmount,
            PayType=string.IsNullOrWhiteSpace(payType)?null:payType.Trim(),
            BizType=bizType,
            OutTradeNo=string.IsNullOrWhiteSpace(outTradeNo)?null:outTradeNo,
            PayStatus=payStatus,
        };
    }

    ///<summary>收款成功落账：状态、渠道、到账时间、通道交易号一次写齐</summary>
    public void MarkPaid(string payType,DateTime payTime,string? transactionNo,decimal? actualAmount=null)
    {
        PayStatus="已支付";
        PayType=string.IsNullOrWhiteSpace(payType)?PayType:payType.Trim();
        PayTime=payTime;
        TransactionNo=string.IsNullOrWhiteSpace(transactionNo)?TransactionNo:transactionNo.Trim();
        if(actualAmount.HasValue) PayAmount=actualAmount.Value;
    }

    ///<summary>关闭未支付的流水（订单取消/超时回收/已由其他渠道收清）</summary>
    public void Cancel()=>PayStatus="已取消";

    ///<summary>选定/切换支付渠道</summary>
    public void SetChannel(string channel)=>PayType=channel.Trim();

    ///<summary>补缴单重新出码：切换支付渠道并按剩余应收重算金额（同一张单可反复换渠道）</summary>
    public void PreparePayment(string channel,decimal amount)
    {
        PayType=channel.Trim();
        PayAmount=amount;
    }

    ///<summary>登记退款：累计退款金额，并按「是否退满」自动落成「已退款/部分退款」</summary>
    public void ApplyRefund(decimal refundedTotal)
    {
        RefundAmount=refundedTotal;
        PayStatus=refundedTotal>=PayAmount?"已退款":"部分退款";
    }
}

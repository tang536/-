namespace ERental.DTOs;

/// <summary>
/// 结算记录列表行。
///
/// 扁平的列表投影，不直接返回 <see cref="Models.SettleRecord"/> 实体：
/// 实体会把 <c>Include</c> 进来的整棵对象图（RentalOrder → Customer → 该客户的全部历史订单）
/// 一并序列化，23 条记录就有 128KB，攒到几百条会变成几 MB 的无用负载。
/// 对账表只需要订单号、车牌、客户名三个字段。
/// </summary>
public class SettleListDto
{
    public int SettleId { get; set; }
    public int OrderId { get; set; }
    public decimal ActualRent { get; set; }
    public decimal OverdueFee { get; set; }
    public decimal DamageFee { get; set; }
    public decimal RefundAmount { get; set; }
    public decimal SupplementAmount { get; set; }
    /// <summary>已到账的补缴合计（现金/线上都算），用于判断这张单是否真的收清了</summary>
    public decimal SupplementPaid { get; set; }
    /// <summary>补缴是否已收清：true = 不必再显示收款/催缴入口</summary>
    public bool SupplementCollected { get; set; }
    /// <summary>最近一次补缴收款渠道（现金/支付宝/微信/银行卡）</summary>
    public string? SupplementChannel { get; set; }
    /// <summary>仍未支付的线上补缴单 id（无则为空）</summary>
    public int? SupplementPendingPayId { get; set; }
    public DateTime SettleTime { get; set; }
    public int? OperatorId { get; set; }
    public string? Remark { get; set; }
    /// <summary>订单摘要，保持 rentalOrder 这一层是为了前端取值路径不变</summary>
    public SettleOrderBriefDto? RentalOrder { get; set; }
}

public class SettleOrderBriefDto
{
    public string? OrderNo { get; set; }
    public string? VehicleNo { get; set; }
    /// <summary>客户姓名（Customer.FullName 的扁平投影）</summary>
    public string? CustomerName { get; set; }
}

/// <summary>
/// 补缴账单：一笔结算单产生的「未缴清补缴款」。
///
/// ── 为什么单独一个 DTO ──
/// 前台需要的不是"结算单"本身，而是**这张单还欠多少钱、现在能不能收、怎么收**。
/// 结算单里的 supplement_amount 只说明"应收"，收没收到必须看支付记录，
/// 所以这里把「应收 / 已收 / 是否收清 / 最近渠道 / 待支付单号」一次给全，
/// 前端据此决定显示「收款」还是「已收清」，不需要自己在两个接口之间拼状态。
///
/// ── 状态是派生的，不存列 ──
/// 收清与否 = 已到账补缴合计 ≥ 应收补缴。这样现金收款、支付宝异步回调、
/// 顾客端在线补缴、查单兜底四条路径都自动一致，不会出现"钱收了但状态没改"的脏数据。
/// </summary>
public class SupplementBillDto
{
    public int SettleId { get; set; }
    public int OrderId { get; set; }
    public string OrderNo { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string VehicleNo { get; set; } = "";

    /// <summary>应收补缴金额（结算单上的 supplement_amount）</summary>
    public decimal Amount { get; set; }
    /// <summary>已到账补缴合计（payment_record 里 biz_type='supplement' 且已支付的金额之和）</summary>
    public decimal PaidAmount { get; set; }
    /// <summary>是否已收清；false 即「待收款」，也是前台入口唯一的显示条件</summary>
    public bool Collected { get; set; }
    /// <summary>最近一次收款的渠道（现金/支付宝/微信/银行卡）；未收过为空</summary>
    public string? Channel { get; set; }
    /// <summary>仍未支付的线上补缴单（有则可继续拉起付款；现金收款时为空）</summary>
    public int? PendingPayId { get; set; }
    /// <summary>待支付单当前选定的渠道</summary>
    public string? PendingChannel { get; set; }

    /// <summary>逾期费与逾期小时数：让前台看到"欠的钱是超时产生的"，收款时有据可依</summary>
    public decimal OverdueFee { get; set; }
    public int OverdueHours { get; set; }
    public DateTime SettleTime { get; set; }
    public DateTime? PaidTime { get; set; }
}

/// <summary>
/// 收款请求：一部手机/一台收银机上前台能提供的四种收款方式。
/// 现金走当场确认（直接落已支付记录）；另外三种拉起真实/模拟通道。
/// </summary>
public class SupplementCollectDto
{
    /// <summary>现金 / 支付宝 / 微信 / 银行卡</summary>
    public string Channel { get; set; } = "现金";
}

/// <summary>收款结果：现金直接给出 Paid=true；线上渠道给出收银台地址/二维码供展示与轮询。</summary>
public class SupplementCollectResultDto
{
    public int SettleId { get; set; }
    public int? PayId { get; set; }
    public string Channel { get; set; } = "";
    public decimal Amount { get; set; }
    /// <summary>是否已收清（现金收款为 true，线上渠道付款成功后才变 true）</summary>
    public bool Paid { get; set; }
    public string PayUrl { get; set; } = "";
    public string QrCode { get; set; } = "";
    public string PayMode { get; set; } = "";
    /// <summary>是否真实通道（false = 模拟收银台，页面需明确提示"不会真实扣款"）</summary>
    public bool RealPay { get; set; }
    public string Message { get; set; } = "";
}

namespace ERental.DTOs;

/// <summary>
/// 支付记录出参。
/// 为什么不直接返回 EF 实体：<c>PaymentRecord</c> 带 <c>RentalOrder</c> 导航，
/// 而订单又带 <c>Customer</c> + <c>Vehicle</c>，直接序列化会把整棵对象图吐给前端——
/// 实测财务调用 <c>GET /api/payment</c> 时会拿到客户的登录账号、身份证号、
/// 注册时间，以及 <c>rentalOrders: [null]</c> 这类关系修复产生的噪音字段。
/// 这既违反本项目「接口出入参一律用 DTO」的约定，也让财务看到了业务上不需要的客户证件信息。
/// 这里按「财务对账」真正需要的字段做扁平投影。
/// </summary>
public class PaymentDto
{
    public int PayId { get; init; }
    public int OrderId { get; init; }
    /// <summary>关联订单号（对账时用于核对到具体订单）</summary>
    public string OrderNo { get; init; } = string.Empty;
    /// <summary>客户姓名（对账展示用，不含证件信息）</summary>
    public string CustomerName { get; init; } = string.Empty;
    public string VehicleNo { get; init; } = string.Empty;
    public decimal PayAmount { get; init; }
    public string PayType { get; init; } = string.Empty;
    public string PayStatus { get; init; } = string.Empty;
    public decimal RefundAmount { get; init; }
    /// <summary>第三方交易流水号（微信/支付宝扫码支付时由支付渠道返回）</summary>
    public string TransactionNo { get; init; } = string.Empty;
    public DateTime? PayTime { get; init; }
}

/// <summary>
/// 内置模拟收银台通知的处理结果。
/// 为什么真实回调返回纯文本 success/failure，这里却要一个 DTO？
/// 因为真实回调的"读者"是支付宝服务器（只认两个单词），
/// 而模拟通知的读者是**本系统的前端**——它需要把失败原因原样显示给用户，
/// 否则用户只能看到一句「支付通知未成功受理，请重试」，无从判断是
/// 生产模式不允许、订单状态不对，还是金额对不上。
/// </summary>
public class MockNotifyResult
{
    public bool Accepted { get; init; }
    /// <summary>可直接展示给用户的说明文案</summary>
    public string Message { get; init; } = string.Empty;

    public static MockNotifyResult Ok(string msg) => new() { Accepted = true, Message = msg };
    public static MockNotifyResult Fail(string msg) => new() { Accepted = false, Message = msg };
}

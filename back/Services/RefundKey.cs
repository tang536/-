namespace ERental.Services;

/// <summary>
/// 退款请求号的统一构造规则（需求 4.2.5「剩余押金原路退还」）。
///
/// 三个通道用同一口径，只是字段名不同：
///   支付宝 <c>out_request_no</c>、微信 <c>out_refund_no</c>、银行卡 <c>out_refund_no</c>。
///
/// 规则：<c>{payId}R{累计应退金额的分}</c>，例如 <c>128R15000</c> 表示「第 128 条支付记录累计退了 150.00 元」。
/// 为什么用**累计**金额而不是本次金额：
///   网关侧幂等以请求号为准。若用「本次金额」，一笔 100 元分三次各退 20 元会产生三个不同请求号，
///   但第一次成功后本地事务回滚重试时，会再发一次 20 元的请求号 —— 网关无法识别这是重试，
///   存在重复退款风险。用累计额则「退到 20 元」只对应一个请求号，重试天然幂等。
/// 金额先四舍五入到分再转整数，避免 0.1+0.2 这类浮点残差造出 <c>R2999</c> 与 <c>R3000</c> 两个键。
/// </summary>
public static class RefundKey
{
    /// <summary>按累计应退金额生成请求号</summary>
    /// <param name="payId">支付记录 ID（不是订单 ID）</param>
    /// <param name="cumulativeAmount">**累计**已退金额（含本次），单位元</param>
    public static string For(int payId, decimal cumulativeAmount)
    {
        // decimal.Round 用银行家舍入会让 .005 变成 .00，这里用 AwayFromZero 与财务口径一致
        var cents = (int)decimal.Round(cumulativeAmount * 100m, 0, MidpointRounding.AwayFromZero);
        return $"{payId}R{cents}";
    }
}

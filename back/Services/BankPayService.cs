using System.Security.Cryptography;
using System.Text;

namespace ERental.Services;

/// <summary>
/// 银行卡支付服务（正式通道）：对接**通用收银网关**协议的跳转支付。
///
/// 为什么是「通用网关」而不是某家具体银行：
///   银行卡收单没有像支付宝/微信那样的唯一官方协议——银行直连、银联全渠道、
///   第三方聚合收单（如常见聚合支付平台）三家的报文各不相同。
///   本服务按业内最通行的「HTTP 跳转网关」协议实现：
///     ① 下单：把商户号 / 订单号 / 金额 / 回调地址等参数按 key 字典序排列，
///        拼成 k=v&amp; 串后用签名密钥做 HMAC-SHA256，签名随参数一起重定向到网关；
///     ② 回调：网关 POST 同构参数过来，按**同一套签名规则**验签，通过才入账；
///     ③ 入账：与支付宝 / 微信共用 <see cref="AlipayService.ApplyTradeResultAsync"/>。
///   选 HMAC-SHA256 而非 MD5：MD5 签名的聚合网关大量存在，但 HMAC 是当前的主流要求，
///   且 .NET 内置实现无依赖；如实际接入的机构要求 MD5，只需替换 <see cref="BuildSign"/> 一处。
///
/// 主动查单：通用网关没有统一查询协议，本通道**不实现查单**，
///   入账依赖异步回调（配置的 bank_notify_url 必须公网可达），这点与支付宝/微信不同，
///   在通道状态详情里会明确提示。
///
/// 退款：按同一套签名规则 POST 到退款地址（<c>pay.bank_refund_url</c>，未填时由支付网关推导），
///   幂等键 out_refund_no 由调用方生成；响应做宽容解析，解析不出明确成功信号即判失败。
/// </summary>
public class BankPayService(
    PayConfigService payConfig, AlipayService alipay, IHttpClientFactory httpFactory, ILogger<BankPayService> logger)
{
    private readonly PayConfigService _payConfig = payConfig;
    private readonly AlipayService _alipay = alipay;
    private readonly IHttpClientFactory _httpFactory = httpFactory;
    private readonly ILogger<BankPayService> _logger = logger;

    /// <summary>银行卡通道的一次运行参数。</summary>
    public sealed record BankRuntime(
        string MerchantNo, string Gateway, string SignKey, string NotifyUrl, string ReturnUrl, string RefundUrl)
    {
        /// <summary>三项必填是否齐全（商户号 + 网关 + 签名密钥）</summary>
        public bool CredentialsComplete =>
            !string.IsNullOrWhiteSpace(MerchantNo) && !string.IsNullOrWhiteSpace(Gateway)
            && !string.IsNullOrWhiteSpace(SignKey);
    }

    public async Task<(BankRuntime Rt, bool Real)> ResolveAsync()
    {
        var raw = await _payConfig.GetRawAsync();
        string Get(string key) => raw.GetValueOrDefault(key, "").Trim();
        string Secret(string key) => _payConfig.Secret(key, Get(key));

        var enabled = Get(PayConfigService.KeyBankEnabled) is "1" or "true" or "True";
        var gateway = Get(PayConfigService.KeyBankGateway);
        var rt = new BankRuntime(
            Get(PayConfigService.KeyBankMerchantNo),
            gateway,
            Secret(PayConfigService.KeyBankSignKey),
            _payConfig.FallbackChannelUrl(Get(PayConfigService.KeyBankNotifyUrl), "api/payment/bank/notify"),
            _payConfig.FallbackChannelUrl(Get(PayConfigService.KeyReturnUrl), "pay-result"),
            DeriveRefundUrl(Get(PayConfigService.KeyBankRefundUrl), gateway));

        return (rt, enabled && rt.CredentialsComplete);
    }

    /// <summary>
    /// 退款地址：优先用管理员显式填写的 <c>pay.bank_refund_url</c>。
    /// 未填时按业内常见约定由支付网关推导——以 <c>/pay</c> 结尾的网关换成 <c>/refund</c>，
    /// 其余情况追加 <c>/refund</c>。推导不符合所选机构的路径规范时，请直接填退款地址。
    /// </summary>
    private static string DeriveRefundUrl(string configured, string gateway)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        var g = gateway.Trim().TrimEnd('/');
        if (g.Length == 0) return "";
        if (g.EndsWith("/pay", StringComparison.OrdinalIgnoreCase))
            return g[..^3] + "refund";
        return g + "/refund";
    }

    /// <summary>
    /// 创建支付：凭证齐全 → 构造带签名的网关跳转 URL；否则退回内置模拟收银台。
    /// 参数按 key 字典序拼串并 HMAC-SHA256 签名（回调验签用同一套规则）。
    /// </summary>
    public async Task<(string PayUrl, bool Real)> CreatePayAsync(
        string orderNo, string subject, decimal amount)
    {
        var (rt, real) = await ResolveAsync();
        if (!real)
        {
            _logger.LogInformation("银行卡凭证不全，订单 {OrderNo} 使用内置模拟收银台", orderNo);
            return (WechatPayService.MockUrl(orderNo, amount, subject, "银行卡"), false);
        }

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["merchant_no"] = rt.MerchantNo,
            ["out_trade_no"] = orderNo,
            ["subject"] = subject,
            ["total_amount"] = amount.ToString("0.00"),
            ["currency"] = "CNY",
            ["notify_url"] = rt.NotifyUrl,
            ["return_url"] = rt.ReturnUrl,
            ["timestamp"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString()
        };
        var sign = BuildSign(parameters, rt.SignKey);
        parameters["sign"] = sign;

        var query = string.Join("&", parameters.Select(kv =>
            $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var payUrl = rt.Gateway.TrimEnd('/') + (rt.Gateway.Contains('?') ? "&" : "?") + query;
        _logger.LogInformation("已为订单 {OrderNo} 生成银行卡网关跳转链接（金额 {Amount}）", orderNo, amount);
        return (payUrl, true);
    }

    /// <summary>
    /// 处理银行卡网关异步回调：验签 → 入账。
    /// 返回 (是否受理, 说明)。控制器按结果回文本 "success"/"failure"（与支付宝回调同约定）。
    /// </summary>
    public async Task<(bool Ok, string Message)> HandleNotifyAsync(IDictionary<string, string> form)
    {
        var (rt, real) = await ResolveAsync();
        if (!real)
            return (false, "银行卡通道未配置凭证，不接受回调。");

        // 验签：除 sign 外的全部参数按同一规则重算 HMAC，与回调带来的 sign 恒等比较
        var expected = BuildSign(
            new SortedDictionary<string, string>(form.ToDictionary(kv => kv.Key, kv => kv.Value ?? ""), StringComparer.Ordinal),
            rt.SignKey);
        var actual = form.TryGetValue("sign", out var s) ? s : "";
        if (string.IsNullOrEmpty(actual) || !FixedTimeEquals(expected, actual))
        {
            form.TryGetValue("out_trade_no", out var bad);
            _logger.LogWarning("银行卡回调验签失败（订单 {OrderNo}，回调参数键：{Keys}）",
                bad ?? "(无)", string.Join(",", form.Keys.OrderBy(k => k, StringComparer.Ordinal)));
            return (false, "回调验签失败。");
        }

        // 字段名对齐入账契约：网关侧 trade_status=SUCCESS 表示付款成功
        return await _alipay.ApplyTradeResultAsync(new Dictionary<string, string>
        {
            ["out_trade_no"] = form.TryGetValue("out_trade_no", out var o) ? o : "",
            ["trade_status"] = form.TryGetValue("trade_status", out var st) ? st : "",
            ["trade_no"] = form.TryGetValue("trade_no", out var tn) ? tn : "",
            ["total_amount"] = form.TryGetValue("total_amount", out var amt) ? amt : ""
        }, "银行卡");
    }

    /// <summary>
    /// 银行卡退款：按同一套 HMAC 签名规则 POST 到退款地址。
    ///
    /// 通用网关没有统一的退款响应规范，这里做**宽容解析**：
    ///   ① JSON 响应 → 取 code / status / result 任一字段，命中 0 / 0000 / 200 / SUCCESS / PROCESSING 即受理；
    ///   ② 纯文本响应 → 出现 success/succeed/ok（不区分大小写）即受理；
    ///   ③ 其余情况一律判失败，**绝不把"没解析明白"当成"退款成功"**，避免账面已退、钱没退。
    ///
    /// 幂等键 <paramref name="outRefundNo"/> 与支付宝/微信同构，由调用方按「支付记录 + 累计退款额」生成。
    /// </summary>
    public async Task<(bool Ok, string Message)> RefundAsync(
        string outTradeNo, string outRefundNo, decimal amount, decimal totalAmount, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(outTradeNo))
            return (false, "缺少银行卡支付流水号，无法原路退款；请到收单机构后台按订单号手工退款。");
        if (amount <= 0) return (false, "退款金额必须大于 0。");

        var (rt, real) = await ResolveAsync();
        if (!real) return (false, "银行卡通道未配置凭证，未发生真实资金划转。");
        if (string.IsNullOrWhiteSpace(rt.RefundUrl))
            return (false, "未配置银行卡退款地址，无法发起退款。");

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["merchant_no"] = rt.MerchantNo,
            ["out_trade_no"] = outTradeNo,
            ["out_refund_no"] = outRefundNo,
            ["refund_amount"] = amount.ToString("0.00"),
            ["total_amount"] = totalAmount.ToString("0.00"),
            ["reason"] = string.IsNullOrWhiteSpace(reason) ? "" : SecretMasker.Truncate(reason.Trim(), 100),
            ["timestamp"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString()
        };
        parameters["sign"] = BuildSign(parameters, rt.SignKey);

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var resp = await http.PostAsync(rt.RefundUrl,
                new FormUrlEncodedContent(parameters.ToDictionary(kv => kv.Key, kv => kv.Value)));
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("银行卡退款失败：HTTP {Status}", (int)resp.StatusCode);
                return (false, $"银行卡退款被网关拒绝（HTTP {(int)resp.StatusCode}），请核对商户凭证与退款地址。");
            }

            var (accepted, status) = IsAcceptedResponse(body);
            if (!accepted)
            {
                _logger.LogWarning("银行卡退款未获受理：{Status}", status);
                return (false, $"银行卡退款未获受理（{status}），请到收单机构后台核实。");
            }
            _logger.LogInformation("银行卡退款已受理：{Status}", status);
            return (true, $"银行卡退款已受理（{status}）");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("银行卡退款请求异常（{ExType}）", ex.GetType().Name);
            return (false, "银行卡退款网关不可达，请检查网络与退款地址配置。");
        }
        catch (TaskCanceledException)
        {
            return (false, "银行卡退款请求超时，结果未知：请到收单机构后台核实后再重试。");
        }
    }

    /// <summary>
    /// 宽容判定网关是否受理退款：JSON 取 code/status/result，纯文本看关键字。
    /// 解析不出明确成功信号时**一律判失败**——宁可让人去对账，也不能谎报退款成功。
    /// </summary>
    private static (bool Accepted, string Status) IsAcceptedResponse(string body)
    {
        var text = (body ?? "").Trim();
        if (text.Length == 0) return (false, "网关返回空响应");

        if (text.StartsWith('{') || text.StartsWith('['))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                foreach (var name in new[] { "code", "status", "result", "ret_code", "return_code" })
                {
                    if (!doc.RootElement.TryGetProperty(name, out var el)) continue;
                    // 不用 switch 表达式：ValueKind 是 8 值枚举，缺省会触发 IDE0072（本项目要求 0 警告）
                    var kind = el.ValueKind;
                    var value = kind == System.Text.Json.JsonValueKind.String ? (el.GetString() ?? "")
                              : kind == System.Text.Json.JsonValueKind.Number ? el.GetRawText()
                              : kind == System.Text.Json.JsonValueKind.True ? "true"
                              : "";
                    var ok = value.ToUpperInvariant() is "0" or "0000" or "00" or "200"
                        or "SUCCESS" or "SUCCEED" or "PROCESSING" or "TRUE";
                    if (value.Length > 0) return (ok, $"{name}={value}");
                }
                return (false, "网关响应中无状态字段");
            }
            catch (System.Text.Json.JsonException)
            {
                return (false, "网关响应不是合法 JSON");
            }
        }

        var lower = text.ToLowerInvariant();
        if (lower.Contains("success") || lower.Contains("succeed") || lower == "ok")
            return (true, SecretMasker.Truncate(text, 30));
        return (false, SecretMasker.Truncate(text, 30));
    }

    /// <summary>按 key 字典序拼 k=v&amp;（末尾直接接签名密钥）后做 HMAC-SHA256，返回大写十六进制</summary>
    private static string BuildSign(SortedDictionary<string, string> parameters, string signKey)
    {
        var content = string.Join("&", parameters
            .Where(kv => kv.Key != "sign" && !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{kv.Key}={kv.Value}")) + signKey;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(content)));
    }

    /// <summary>恒时比较签名：避免逐字节短路让攻击者能按位猜签名（时序侧信道）</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}

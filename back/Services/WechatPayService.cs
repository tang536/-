using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERental.Data;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>
/// 微信支付 APIv3 收单服务（正式通道）：H5 下单、订单查询、异步回调验签与解密入账。
///
/// 为什么选 **H5 支付**（transactions/h5）而不是 Native 扫码：
///   Native 返回 code_url（weixin:// 二维码串），前端必须渲染二维码、轮询等用户扫；
///   H5 返回 h5_url（https://wx.tenpay.com/...），可以直接 window.location 跳过去，
///   与支付宝「电脑网站支付」的页面跳转模式同构——支付页 / 结果页 / 轮询兜底三处
///   前端逻辑无需按渠道分叉。
///
/// 与支付宝通道的**同构约定**（三通道共用同一套业务语义）：
///   1. 凭证齐全 → 走真实通道；不齐全 → 退回内置模拟收银台（与三级降级同思路，
///      但判定条件是「本通道自己的必填项」，不与支付宝的三级降级互相干扰）；
///   2. 入账**只认验签/解密过的支付数据**，且全部汇入
///      <see cref="AlipayService.ApplyTradeResultAsync"/>（幂等 + 金额校验 + 写支付记录）；
///   3. 沙箱/开发环境回调打不进本机 → 前端轮询触发的主动查单兜底入账。
///
/// ⚠️ 签名规格：微信 APIv3 的请求签名放在 **Authorization 头**（不是参数里），
///   格式 `WECHATPAY2-SHA256-RSA2048 mchid="...",nonce_str="...",timestamp="...",serial_no="...",signature="..."`，
///   待签串为五行的报文摘要（方法/路径/时间戳/随机串/报文体），商户私钥 SHA256withRSA。
///   回调验签用「微信支付公钥」（公钥模式，serial 前缀 PUB）或平台证书公钥，
///   通知体 resource 需要 **AES-256-GCM**（密钥 = APIv3 密钥）解密。
/// </summary>
public class WechatPayService(
    AppDbContext db, PayConfigService payConfig,
    AlipayService alipay, IHttpClientFactory httpFactory, ILogger<WechatPayService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly PayConfigService _payConfig = payConfig;
    private readonly AlipayService _alipay = alipay;
    private readonly IHttpClientFactory _httpFactory = httpFactory;
    private readonly ILogger<WechatPayService> _logger = logger;

    /// <summary>微信支付 APIv3 默认网关。管理员未填网关时用它。</summary>
    private const string DefaultGateway = "https://api.mch.weixin.qq.com";

    /// <summary>一次微信支付调用所需的运行参数（由配置解析而来）。</summary>
    public sealed record WechatRuntime(
        string AppId, string MchId, string ApiV3Key, string CertSerial,
        string PrivateKey, string NotifyUrl, string Gateway)
    {
        /// <summary>五项必填凭证是否齐全（缺任何一项都无法完成 APIv3 签名/解密）</summary>
        public bool CredentialsComplete =>
            !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(MchId)
            && !string.IsNullOrWhiteSpace(ApiV3Key) && !string.IsNullOrWhiteSpace(CertSerial)
            && !string.IsNullOrWhiteSpace(PrivateKey);
    }

    /// <summary>
    /// 读取本通道凭证并判定是否可走真实通道。
    /// 敏感项沿用 SecretProvider 分层解析（env > secrets > db），与支付宝口径一致。
    /// </summary>
    public async Task<(WechatRuntime Rt, bool Real)> ResolveAsync()
    {
        var raw = await _payConfig.GetRawAsync();
        string Get(string key) => raw.GetValueOrDefault(key, "").Trim();
        string Secret(string key) => _payConfig.Secret(key, Get(key));

        var enabled = Get(PayConfigService.KeyWechatEnabled) is "1" or "true" or "True";
        var rt = new WechatRuntime(
            Get(PayConfigService.KeyWechatAppId),
            Get(PayConfigService.KeyWechatMchId),
            Secret(PayConfigService.KeyWechatApiV3Key),
            Get(PayConfigService.KeyWechatCertSerial),
            Secret(PayConfigService.KeyWechatPrivateKey),
            _payConfig.FallbackChannelUrl(Get(PayConfigService.KeyWechatNotifyUrl), "api/payment/wechat/notify"),
            Get(PayConfigService.KeyWechatGateway).Length > 0
                ? Get(PayConfigService.KeyWechatGateway)
                : DefaultGateway);

        return (rt, enabled && rt.CredentialsComplete);
    }

    /// <summary>
    /// 创建支付：凭证齐全 → 微信 H5 下单，返回可跳转的收银台 URL；
    /// 否则 → 内置模拟收银台（带 payType=微信 标记，回调按微信入账）。
    /// </summary>
    /// <param name="orderNo">商户订单号（即 out_trade_no，用于微信侧对账与回调查单）</param>
    /// <param name="subject">商品描述（展示在用户微信收银台上的订单标题）</param>
    /// <param name="amount">支付金额（单位：元；内部换算成分提交微信）</param>
    /// <param name="returnUrl">支付完成后引导用户返回的前端地址（拼进 h5_url 的 redirect_url）</param>
    public async Task<(string PayUrl, bool Real)> CreatePayAsync(
        string orderNo, string subject, decimal amount, string returnUrl)
    {
        var (rt, real) = await ResolveAsync();
        if (!real)
        {
            _logger.LogInformation("微信支付凭证不全，订单 {OrderNo} 使用内置模拟收银台", orderNo);
            return (MockUrl(orderNo, amount, subject), false);
        }

        // 金额单位：微信用**分**（整数），支付宝用元——转换点只在这里，后面全部元
        var totalFen = (int)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["appid"] = rt.AppId,
            ["mchid"] = rt.MchId,
            ["description"] = subject,
            ["out_trade_no"] = orderNo,
            ["notify_url"] = rt.NotifyUrl,
            ["amount"] = new Dictionary<string, object?> { ["total"] = totalFen, ["currency"] = "CNY" },
            ["scene_info"] = new Dictionary<string, object?>
            {
                // H5 支付必填：用户发起支付的终端 IP + 场景类型
                ["payer_client_ip"] = ClientIp(),
                ["h5_info"] = new Dictionary<string, object?> { ["type"] = "Wap" }
            }
        });

        var url = $"{rt.Gateway.TrimEnd('/')}/v3/pay/transactions/h5";
        var respBody = await PostSignedAsync(rt, url, body);

        string? h5Url = null;
        try
        {
            using var doc = JsonDocument.Parse(respBody);
            if (doc.RootElement.TryGetProperty("h5_url", out var el))
                h5Url = el.GetString();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("微信 H5 下单响应解析失败（{ExType}）", ex.GetType().Name);
        }

        if (string.IsNullOrWhiteSpace(h5Url))
        {
            // 只回类型不回响应体：响应体可能回显报文片段，不能进日志/异常消息
            throw new Exception("微信下单未返回收银台地址，请检查商户凭证与网关配置");
        }

        // redirect_url：用户在微信侧完成/关闭支付后点「返回商家」落回的前端页（需 URL 编码）
        var sep = h5Url.Contains('?') ? "&" : "?";
        var payUrl = $"{h5Url}{sep}redirect_url={Uri.EscapeDataString(returnUrl)}";
        _logger.LogInformation("已为订单 {OrderNo} 生成微信 H5 收银台链接（金额 {Amount}）", orderNo, amount);
        return (payUrl, true);
    }

    /// <summary>
    /// 主动查询微信订单状态（前端轮询兜底，与支付宝查单同思路）。
    /// trade_state=SUCCESS 时调用共享入账逻辑；金额从分换算回元再送入校验。
    /// </summary>
    public async Task<string> QueryTradeStatusAsync(string orderNo)
    {
        var order = await _db.RentalOrders.FirstOrDefaultAsync(o => o.OrderNo == orderNo);
        if (order == null) return "订单不存在";

        var (rt, real) = await ResolveAsync();
        if (!real) return order.OrderStatus;   // 模拟模式：本地状态即真相

        string tradeState, transactionId; int totalFen;
        try
        {
            (tradeState, transactionId, totalFen) = await QueryRawAsync(rt, orderNo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("查询微信订单 {OrderNo} 失败（{ExType}），回退本地状态", orderNo, ex.GetType().Name);
            return order.OrderStatus;
        }

        if (tradeState == "SUCCESS")
        {
            var (ok, msg) = await _alipay.ApplyTradeResultAsync(new Dictionary<string, string>
            {
                ["out_trade_no"] = orderNo,
                ["trade_status"] = "TRADE_SUCCESS",
                ["trade_no"] = transactionId,
                ["total_amount"] = (totalFen / 100m).ToString("0.00")
            }, "微信");
            _logger.LogInformation("微信查单兜底入账 {OrderNo}：{Result}（{Message}）", orderNo, ok ? "成功" : "拒绝", msg);
            return ok ? "已支付" : order.OrderStatus;
        }
        // 其余状态（NOTPAY / USERPAYING / 未知）都以本地订单状态为准，无需再分支
        return tradeState == "CLOSED" ? "已关闭" : order.OrderStatus;
    }

    /// <summary>
    /// 处理微信异步回调：验签（微信支付公钥）→ AES-256-GCM 解密 resource → 入账。
    /// 返回 (是否受理, 说明)。控制器按结果回 200 / 4xx——微信对非 2xx 会按策略重推。
    /// </summary>
    /// <param name="headers">回调请求头（Wechat-Timestamp / Wechat-Nonce / Wechat-Signature / Wechat-Serial）</param>
    /// <param name="body">回调原始 JSON 报文（验签必须用**原始字节**对应的文本，不能反序列化再拼）</param>
    public async Task<(bool Ok, string Message)> HandleNotifyAsync(
        IDictionary<string, string> headers, string body)
    {
        var (rt, real) = await ResolveAsync();
        if (!real)
            return (false, "微信支付未配置凭证，不接受回调。");

        // ① 验签：待签串 = 时间戳\n随机串\n报文体\n，用「微信支付公钥」验 RSA-SHA256。
        //    公钥模式下管理员在商户平台「API 安全」里可同时拿到公钥串与公钥序号（PUB 开头），
        //    这里要求二者配对填写（公钥序号填在 wechat_cert_serial，公钥填在 wechat_platform_key）。
        var platformKey = _payConfig.Secret(PayConfigService.KeyWechatPlatformKey,
            (await _payConfig.GetRawAsync()).TryGetValue(PayConfigService.KeyWechatPlatformKey, out var pk) ? pk.Trim() : "");
        if (string.IsNullOrWhiteSpace(platformKey))
            return (false, "未配置微信支付公钥，无法验签回调。");

        var ts = headers.TryGetValue("Wechat-Timestamp", out var v1) ? v1 : "";
        var nonce = headers.TryGetValue("Wechat-Nonce", out var v2) ? v2 : "";
        var sign = headers.TryGetValue("Wechat-Signature", out var v3) ? v3 : "";
        var serial = headers.TryGetValue("Wechat-Serial", out var v4) ? v4 : "";
        var message = $"{ts}\n{nonce}\n{body}\n";
        if (!AlipaySigner.Verify(message, sign, platformKey))
        {
            _logger.LogWarning("微信回调验签失败（serial {Serial}）", serial);
            return (false, "回调验签失败。");
        }

        // ② 解密 resource：AES-256-GCM，密钥 = APIv3 密钥，nonce/AD 来自报文
        string outTradeNo, tradeNo, tradeState; int totalFen;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var res = doc.RootElement.GetProperty("resource");
            var cipher = res.GetProperty("ciphertext").GetString() ?? "";
            var resNonce = res.GetProperty("nonce").GetString() ?? "";
            var ad = res.TryGetProperty("associated_data", out var adEl) ? adEl.GetString() ?? "" : "";
            var plain = AesGcmDecrypt(rt.ApiV3Key, resNonce, ad, cipher);

            using var r = JsonDocument.Parse(plain);
            string G(string n) => r.RootElement.TryGetProperty(n, out var e) ? e.GetString() ?? "" : "";
            outTradeNo = G("out_trade_no");
            tradeNo = G("transaction_id");
            tradeState = G("trade_state");
            totalFen = r.RootElement.TryGetProperty("amount", out var amt) &&
                       amt.TryGetProperty("total", out var t) && t.TryGetInt32(out var ti) ? ti : 0;
        }
        catch (CryptographicException)
        {
            return (false, "回调报文解密失败：请核对 APIv3 密钥。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("微信回调解析失败（{ExType}）", ex.GetType().Name);
            return (false, "回调报文解析失败。");
        }

        // ③ 入账：与支付宝回调共用同一实现（幂等 + 金额校验）
        return await _alipay.ApplyTradeResultAsync(new Dictionary<string, string>
        {
            ["out_trade_no"] = outTradeNo,
            ["trade_status"] = tradeState == "SUCCESS" ? "TRADE_SUCCESS" : tradeState,
            ["trade_no"] = tradeNo,
            ["total_amount"] = (totalFen / 100m).ToString("0.00")
        }, "微信");
    }

    /// <summary>查询微信订单原始结果（HTTP/解析异常向上抛）</summary>
    private async Task<(string TradeState, string TransactionId, int TotalFen)> QueryRawAsync(
        WechatRuntime rt, string outTradeNo)
    {
        var url = $"{rt.Gateway.TrimEnd('/')}/v3/pay/transactions/out-trade-no/{Uri.EscapeDataString(outTradeNo)}?mchid={Uri.EscapeDataString(rt.MchId)}";
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Remove("Authorization");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", BuildAuthorization(rt, "GET", url, ""));
        http.Timeout = TimeSpan.FromSeconds(10);

        var resp = await http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"微信查询返回 {(int)resp.StatusCode}");

        using var doc = JsonDocument.Parse(body);
        string G(string n) => doc.RootElement.TryGetProperty(n, out var e) ? e.GetString() ?? "" : "";
        var totalFen = doc.RootElement.TryGetProperty("amount", out var amt) &&
                       amt.TryGetProperty("total", out var t) && t.TryGetInt32(out var ti) ? ti : 0;
        return (G("trade_state"), G("transaction_id"), totalFen);
    }

    /// <summary>
    /// 构造 APIv3 请求签名（Authorization 头）。
    /// 待签串固定五行：方法\nURL（含查询串）\n时间戳\n随机串\n报文体\n，商户私钥 SHA256withRSA。
    /// </summary>
    private static string BuildAuthorization(WechatRuntime rt, string method, string url, string body)
    {
        var timestamp = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var message = $"{method}\n{url}\n{timestamp}\n{nonce}\n{body}\n";
        var signature = AlipaySigner.Sign(message, rt.PrivateKey);
        return $"WECHATPAY2-SHA256-RSA2048 mchid=\"{rt.MchId}\",nonce_str=\"{nonce}\","
             + $"timestamp=\"{timestamp}\",serial_no=\"{rt.CertSerial}\",signature=\"{signature}\"";
    }

    /// <summary>带签名头发起 POST，返回响应体</summary>
    private async Task<string> PostSignedAsync(WechatRuntime rt, string url, string body)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Remove("Authorization");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", BuildAuthorization(rt, "POST", url, body));
        http.Timeout = TimeSpan.FromSeconds(15);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await http.PostAsync(url, content);
        var respBody = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            // 响应体可能回显报文片段，不进日志与异常消息，只记状态码
            _logger.LogWarning("微信下单失败：HTTP {Status}", (int)resp.StatusCode);
            throw new Exception($"微信下单被网关拒绝（HTTP {(int)resp.StatusCode}），请核对商户凭证");
        }
        return respBody;
    }

    /// <summary>AES-256-GCM 解密微信回调 resource（密钥 = APIv3 密钥 32 字节）</summary>
    private static byte[] AesGcmDecrypt(string apiV3Key, string nonce, string associatedData, string cipherBase64)
    {
        var key = Encoding.UTF8.GetBytes(apiV3Key);
        if (key.Length != 32) throw new CryptographicException("APIv3 密钥长度须为 32 字节");
        var cipher = Convert.FromBase64String(cipherBase64);
        // 密文末 16 字节是 GCM 认证标签，需从密文中拆出（.NET 的 AesGcm 要求分开传）
        const int tagLen = 16;
        var data = cipher[..^tagLen];
        var tag = cipher[^tagLen..];

        using var aes = new AesGcm(key, tagLen);
        var plain = new byte[data.Length];
        aes.Decrypt(Encoding.UTF8.GetBytes(nonce), data, tag, plain,
            string.IsNullOrEmpty(associatedData) ? null : Encoding.UTF8.GetBytes(associatedData));
        return plain;
    }

    /// <summary>发起支付请求的客户端 IP（H5 下单 scene_info 必填；取不到时给合法占位）</summary>
    private static string ClientIp() =>
        System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?.ToString() ?? "127.0.0.1";

    /// <summary>内置模拟收银台地址（与支付宝模拟口径一致，带 _mock_pay_type 标记渠道）</summary>
    public static string MockUrl(string orderNo, decimal amount, string subject, string payType = "微信") =>
        $"/mock-pay?orderNo={Uri.EscapeDataString(orderNo)}&amount={amount:0.00}" +
        $"&payType={Uri.EscapeDataString(payType)}&subject={Uri.EscapeDataString(subject)}";
}

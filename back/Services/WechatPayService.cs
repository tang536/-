using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERental.Data;
using ERental.DTOs;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>微信支付 APIv3 收单服务（正式通道）：H5 下单、订单查询、异步回调验签与解密入账</summary>
public class WechatPayService(AppDbContext db, PayConfigService payConfig, AlipayService alipay, IHttpClientFactory httpFactory, ILogger<WechatPayService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly PayConfigService _payConfig = payConfig;
    private readonly AlipayService _alipay = alipay;
    private readonly IHttpClientFactory _httpFactory = httpFactory;
    private readonly ILogger<WechatPayService> _logger = logger;

    /// <summary>微信支付 APIv3 默认网关</summary>
    private const string DefaultGateway = "https://api.mch.weixin.qq.com";

    /// <summary>一次微信支付调用所需的运行参数（由配置解析而来）</summary>
    public sealed record WechatRuntime(string AppId, string MchId, string ApiV3Key, string CertSerial, string PrivateKey, string NotifyUrl, string Gateway, string PayMode, bool Enabled)
    {
        /// <summary>五项必填凭证是否齐全（缺任何一项都无法完成 APIv3 签名/解密）</summary>
        public bool CredentialsComplete => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(MchId)
                                                                             && !string.IsNullOrWhiteSpace(ApiV3Key) && !string.IsNullOrWhiteSpace(CertSerial)
                                                                             && !string.IsNullOrWhiteSpace(PrivateKey);

        /// <summary>查余额所需的最小凭证集：商户号 + 证书序列号 + 商户私钥</summary>
        public bool BalanceCredentialsComplete =>
            !string.IsNullOrWhiteSpace(MchId) && !string.IsNullOrWhiteSpace(CertSerial)
            && !string.IsNullOrWhiteSpace(PrivateKey);
    }

    /// <summary>一次下单的结果</summary>
    public sealed record WechatPayResult(string PayUrl, string QrCode, bool Real, string PayMode);

    /// <summary>读取本通道凭证并判定是否可走真实通道</summary>
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
                : DefaultGateway,
            PayConfigService.NormalizeWechatPayMode(Get(PayConfigService.KeyWechatPayMode)),
            enabled);

        return (rt, enabled && rt.CredentialsComplete);
    }

    /// <summary>创建支付：凭证齐全 → 按下单方式走真实通道</summary>
    public async Task<WechatPayResult> CreatePayAsync(
        string orderNo, string subject, decimal amount, string returnUrl)
    {
        var (rt, real) = await ResolveAsync();
        if (!real)
        {
            _logger.LogInformation("微信支付凭证不全，订单 {OrderNo} 使用内置模拟收银台", orderNo);
            return new WechatPayResult(MockUrl(orderNo, amount, subject), "", false, rt.PayMode);
        }

        // 金额单位：微信用**分**（整数），支付宝用元——转换点只在这里，后面全部元
        var totalFen = (int)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

        return rt.PayMode == PayConfigService.WechatPayModeNative
            ? await CreateNativeAsync(rt, orderNo, subject, totalFen)
            : await CreateH5Async(rt, orderNo, subject, totalFen, returnUrl);
    }

    /// <summary>Native 下单：返回 code_url（weixin://wxpay/bizpayurl?pr=</summary>
    private async Task<WechatPayResult> CreateNativeAsync(
        WechatRuntime rt, string orderNo, string subject, int totalFen)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["appid"] = rt.AppId,
            ["mchid"] = rt.MchId,
            ["description"] = subject,
            ["out_trade_no"] = orderNo,
            ["notify_url"] = rt.NotifyUrl,
            ["amount"] = new Dictionary<string, object?> { ["total"] = totalFen, ["currency"] = "CNY" }
        });

        var url = $"{rt.Gateway.TrimEnd('/')}/v3/pay/transactions/native";
        // operation 传具体动作：网关拒绝时日志能直接看出走的是扫码还是 H5，省得靠上下文猜
        var respBody = await PostSignedAsync(rt, url, body, "扫码下单");

        string? codeUrl = null;
        try
        {
            using var doc = JsonDocument.Parse(respBody);
            if (doc.RootElement.TryGetProperty("code_url", out var el))
                codeUrl = el.GetString();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("微信 Native 下单响应解析失败（{ExType}）", ex.GetType().Name);
        }

        if (string.IsNullOrWhiteSpace(codeUrl))
        {
            // 只回类型不回响应体：响应体可能回显报文片段，不能进日志/异常消息
            throw new Exception("微信扫码下单未返回二维码内容，请检查商户凭证、网关配置与 Native 支付权限");
        }

        _logger.LogInformation("已为订单 {OrderNo} 生成微信扫码支付二维码（金额 {Amount}）",
            orderNo, totalFen / 100m);
        return new WechatPayResult("", codeUrl, true, PayConfigService.WechatPayModeNative);
    }

    /// <summary>H5 下单：返回可跳转的收银台地址（h5_url 追加 redirect_url）</summary>
    private async Task<WechatPayResult> CreateH5Async(
        WechatRuntime rt, string orderNo, string subject, int totalFen, string returnUrl)
    {
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
        var respBody = await PostSignedAsync(rt, url, body, "H5 下单");

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
        _logger.LogInformation("已为订单 {OrderNo} 生成微信 H5 收银台链接（金额 {Amount}）",
            orderNo, totalFen / 100m);
        return new WechatPayResult(payUrl, "", true, PayConfigService.WechatPayModeH5);
    }

    /// <summary>主动查询微信订单状态（前端轮询兜底，与支付宝查单同思路）</summary>
    public async Task<string> QueryTradeStatusAsync(string orderNo)
    {
        // orderNo 可能是租赁订单号，也可能是补缴单 out_trade_no（SP- 前缀）。
        var isSupplement = orderNo.StartsWith("SP-");
        var order = isSupplement ? null : await _db.RentalOrders.FirstOrDefaultAsync(o => o.OrderNo == orderNo);
        var suppl = isSupplement ? await _db.PaymentRecords.FirstOrDefaultAsync(p => p.OutTradeNo == orderNo) : null;
        if (!isSupplement && order == null) return "订单不存在";
        if (isSupplement && suppl == null) return "补缴单不存在";
        var localStatus = isSupplement ? suppl!.PayStatus : order!.OrderStatus;

        var (rt, real) = await ResolveAsync();
        if (!real) return localStatus;   // 模拟模式：本地状态即真相

        string tradeState, transactionId; int totalFen;
        try
        {
            (tradeState, transactionId, totalFen) = await QueryRawAsync(rt, orderNo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("查询微信订单 {OrderNo} 失败（{ExType}），回退本地状态", orderNo, ex.GetType().Name);
            return localStatus;
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
            return ok ? "已支付" : localStatus;
        }
        // 其余状态（NOTPAY / USERPAYING / 未知）都以本地状态为准，无需再分支
        return tradeState == "CLOSED" ? "已关闭" : localStatus;
    }

    /// <summary>查询微信支付商户账户余额（管理员看板「支付账户余额」用）</summary>
    public async Task<(bool Ok, decimal? Available, decimal? Pending, string? Error)> QueryBalanceAsync()
    {
        var (rt, _) = await ResolveAsync();
        // 余额只认「商户号 + 证书序列号 + 私钥」这三项（见 BalanceCredentialsComplete），
        // 不因缺 AppId / APIv3 密钥而拒绝——那两项与余额接口无关。
        if (!rt.BalanceCredentialsComplete)
            return (false, null, null, "微信支付余额查询凭证不完整（商户号 / 证书序列号 / 商户私钥）");
        if (!rt.Enabled)
            return (false, null, null, "微信支付开关未启用");

        string? lastError = null;
        foreach (var accountType in new[] { "BASIC", "OPERATION" })
        {
            var url = $"{rt.Gateway.TrimEnd('/')}/v3/merchant/fund/balance/{accountType}";
            try
            {
                var http = _httpFactory.CreateClient();
                http.DefaultRequestHeaders.Remove("Authorization");
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", BuildAuthorization(rt, "GET", url, ""));
                http.Timeout = TimeSpan.FromSeconds(10);

                var resp = await http.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    // 403/404 多为此商户未开通该账户类型的资金流，属正常业务限制，换下一级再试
                    lastError = $"{accountType} 账户返回 HTTP {(int)resp.StatusCode}{ReadWechatErrorCode(body)}";
                    continue;
                }

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("available_amount", out var avail) || !avail.TryGetInt64(out var fen))
                {
                    lastError = $"{accountType} 账户响应无可用余额字段";
                    continue;
                }

                decimal? pending = null;
                if (doc.RootElement.TryGetProperty("pending_amount", out var pend) && pend.TryGetInt64(out var pfen))
                    pending = pfen / 100m;

                return (true, fen / 100m, pending, null);
            }
            catch (Exception ex)
            {
                lastError = ex is TaskCanceledException or OperationCanceledException
                    ? $"{accountType} 账户查询超时"
                    : $"{accountType} 账户查询异常（{ex.GetType().Name}）";
            }
        }

        return (false, null, null, lastError ?? "微信余额查询失败");
    }

    /// <summary>从微信错误响应里取 code（如 NO_AUTH），取不到回空串</summary>
    private static string ReadWechatErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
            {
                var v = code.GetString();
                return string.IsNullOrWhiteSpace(v) ? "" : $"（{v}）";
            }
        }
        catch
        {
            // 非 JSON（网关 HTML 错误页）时忽略，状态码已足够定位
        }
        return "";
    }

    /// <summary>微信支付接线自检：向网关发一次最小查询（假订单号），判断签名/证书是否配对</summary>
    public async Task<ChannelSelfCheckDto> SelfCheckAsync()
    {
        var (rt, _) = await ResolveAsync();
        var enabled = (await _payConfig.GetRawAsync())
            .GetValueOrDefault(PayConfigService.KeyWechatEnabled, "").Trim() is "1" or "true" or "True";

        var dto = new ChannelSelfCheckDto
        {
            Channel = "wechat",
            ChannelName = "微信支付",
            Enabled = enabled,
            Configured = rt.CredentialsComplete,
            MerchantId = rt.MchId,
            Gateway = rt.Gateway
        };

        if (!rt.CredentialsComplete)
        {
            dto.Message = "微信支付凭证不完整：AppId、商户号、APIv3 密钥、证书序列号、商户私钥 五项缺一不可，请补全后再自检。";
            return dto;
        }
        if (!enabled)
        {
            dto.Message = "微信支付开关未启用，自检跳过联网探测。如需验证配置，请先打开「启用微信支付」。";
            return dto;
        }

        // 顺手把私钥格式算出来，方便管理员核对粘贴的是不是完整 PEM
        try { dto.KeyFormat = AlipaySigner.DetectPrivateKeyFormat(rt.PrivateKey); }
        catch { dto.KeyFormat = "无法解析（私钥可能不完整）"; }

        // 用一个必然不存在的订单号发查询：签名通过则网关会回「订单不存在」，这证明签名被接受。
        var outTradeNo = "__SELFCHECK__";
        var url = $"{rt.Gateway.TrimEnd('/')}/v3/pay/transactions/out-trade-no/{Uri.EscapeDataString(outTradeNo)}?mchid={Uri.EscapeDataString(rt.MchId)}";
        try
        {
            var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Remove("Authorization");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", BuildAuthorization(rt, "GET", url, ""));
            http.Timeout = TimeSpan.FromSeconds(15);
            var resp = await http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            // 先脱敏（清掉私钥/APIv3 密钥字面值）再截断：网关回显里可能带签名与证书片段，
            // 这道处理保证「查看原始响应」这个排查入口不会成为密钥外泄通道。
            dto.RawResponse = SecretMasker.Truncate(
                SecretMasker.Scrub(body, rt.PrivateKey, rt.ApiV3Key), 800);

            var status = (int)resp.StatusCode;
            // 401 = 鉴权/验签失败（SIGN_ERROR）；其余（404 订单不存在、200）都说明签名已通过。
            if (status == 401 || body.Contains("SIGN_ERROR") || body.Contains("签名错误") || body.Contains("验签失败"))
            {
                dto.SignatureAccepted = false;
                dto.Message = "微信网关拒绝请求：签名或证书序列号不配对。请核对商户 API 私钥与商户 API 证书序列号" +
                              "（cert_serial 应填商户 API 证书序列号，而非 PUB 开头的微信支付公钥序号）。";
            }
            else
            {
                dto.SignatureAccepted = true;
                dto.Message = "微信网关已接受本次签名，配置正确（返回的业务错误属正常，因为订单号是探测用的假号）。";
            }
            return dto;
        }
        catch (Exception ex)
        {
            // 只回异常类型：HttpRequestException 的 Message 可能带完整网关 URL，TaskCanceledException 则是超时。
            _logger.LogWarning("微信自检请求异常：{ExType}", ex.GetType().Name);
            dto.Message = ex is TaskCanceledException or OperationCanceledException
                ? "自检请求超时：网关在 15 秒内未响应，请检查网络或网关地址。"
                : $"自检请求异常（{ex.GetType().Name}），请检查网络与网关地址是否正确。";
            return dto;
        }
    }

    /// <summary>处理微信异步回调：验签（微信支付公钥）→ AES-256-GCM 解密 resource → 入账</summary>
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

    /// <summary>微信退款（APIv3 POST /v3/refund/domestic/refunds）</summary>
    public async Task<(bool Ok, string Message)> RefundAsync(
        string outTradeNo, string outRefundNo, decimal amount, decimal totalAmount, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(outTradeNo))
            return (false, "缺少微信支付订单号，无法原路退款；请到微信商户平台按订单号手工退款。");
        if (amount <= 0) return (false, "退款金额必须大于 0。");

        var (rt, real) = await ResolveAsync();
        if (!real) return (false, "微信支付未配置凭证，未发生真实资金划转。");

        var refundFen = (int)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
        var totalFen = (int)Math.Round(totalAmount * 100m, MidpointRounding.AwayFromZero);
        if (refundFen > totalFen) return (false, "退款金额不能超过原订单实付金额。");

        var payload = new Dictionary<string, object?>
        {
            // 只传 out_trade_no：与 transaction_id 二选一，同时传会被网关拒绝
            ["out_trade_no"] = outTradeNo,
            ["out_refund_no"] = outRefundNo,
            ["reason"] = string.IsNullOrWhiteSpace(reason) ? null : SecretMasker.Truncate(reason.Trim(), 80),
            ["amount"] = new Dictionary<string, object?>
            {
                ["refund"] = refundFen,
                ["total"] = totalFen,
                ["currency"] = "CNY"
            }
        };

        var url = $"{rt.Gateway.TrimEnd('/')}/v3/refund/domestic/refunds";
        string respBody;
        try
        {
            respBody = await PostSignedAsync(rt, url, JsonSerializer.Serialize(payload), "退款");
        }
        catch (Exception ex)
        {
            // 异常消息已由 PostSignedAsync 泛化（不回显报文），这里只补一句可操作提示
            _logger.LogWarning("微信退款失败：{Message}", ex.Message);
            return (false, ex.Message);
        }

        try
        {
            using var doc = JsonDocument.Parse(respBody);
            string G(string n) => doc.RootElement.TryGetProperty(n, out var e) ? e.GetString() ?? "" : "";
            var status = G("status");
            var refundId = G("refund_id");

            return status switch
            {
                "SUCCESS" => (true, $"微信退款成功（退款单 {refundId}）"),
                // PROCESSING：微信已受理、银行侧在途，钱最终会到账，按成功处理
                "PROCESSING" => (true, $"微信退款已受理，资金在途（退款单 {refundId}）"),
                "CLOSED" => (false, "微信退款被关闭：原订单状态或退款金额不满足退款条件，请到商户平台核实。"),
                "ABNORMAL" => (false, "微信退款异常：请到微信商户平台查看该退款单的处理结果。"),
                _ => (false, $"微信退款返回未知状态（{(status.Length == 0 ? "空" : status)}），请到商户平台核实。")
            };
        }
        catch (JsonException)
        {
            return (false, "微信退款响应解析失败，请到商户平台核实退款状态后再重试。");
        }
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

    /// <summary>构造 APIv3 请求签名（Authorization 头）</summary>
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
    private async Task<string> PostSignedAsync(WechatRuntime rt, string url, string body, string operation = "下单")
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
            _logger.LogWarning("微信{Operation}失败：HTTP {Status}", operation, (int)resp.StatusCode);
            throw new Exception($"微信{operation}被网关拒绝（HTTP {(int)resp.StatusCode}），请核对商户凭证");
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

    /// <summary>发起支付请求的客户端 IP（H5 下单 scene_info 必填</summary>
    private static string ClientIp() =>
        System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?.ToString() ?? "127.0.0.1";

    /// <summary>内置模拟收银台地址（与支付宝模拟口径一致，带 _mock_pay_type 标记渠道）</summary>
    public static string MockUrl(string orderNo, decimal amount, string subject, string payType = "微信") =>
        $"/mock-pay?orderNo={Uri.EscapeDataString(orderNo)}&amount={amount:0.00}" +
        $"&payType={Uri.EscapeDataString(payType)}&subject={Uri.EscapeDataString(subject)}";
}

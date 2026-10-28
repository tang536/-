using System.Security.Cryptography;
using System.Text;
using ERental.DTOs;

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
/// 主动查单 / 余额：通用网关同样没有统一协议，本通道按**业内最常见的路径约定**实现——
///   ① 查单 POST 到 <c>query</c> 端点（/pay → /query，其余追加 /query），参数与下单同构
///      （merchant_no / out_trade_no / timestamp + 同一套 HMAC-SHA256 签名），
///      响应用宽容解析（JSON 或纯文本），只有明确取到「成功状态 + 金额」才入账；
///   ② 余额 POST 到 <c>balance</c> 端点（/pay → /balance），解析 available_amount / balance / total_amount；
///   ③ 两条路径都可用 <c>pay.bank_query_url</c> / <c>pay.bank_balance_url</c> 显式覆盖，
///      换机构不用改代码。地址不通时查单返回空串（调用方保持本地状态）、余额返回明确失败原因，
///      绝不把「没查明白」当成「已付款」。
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
        string MerchantNo, string Gateway, string SignKey, string NotifyUrl, string ReturnUrl,
        string RefundUrl, string QueryUrl, string BalanceUrl)
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
            DeriveEndpointUrl(Get(PayConfigService.KeyBankRefundUrl), gateway, "refund"),
            DeriveEndpointUrl(Get(PayConfigService.KeyBankQueryUrl), gateway, "query"),
            DeriveEndpointUrl(Get(PayConfigService.KeyBankBalanceUrl), gateway, "balance"));

        return (rt, enabled && rt.CredentialsComplete);
    }

    /// <summary>
    /// 推导网关端点：优先用管理员显式填写的地址；未填时按业内常见约定推导——
    /// 以 <c>/pay</c>（可带 <c>.do</c>）结尾的网关把最后一段换成目标动作，
    /// 其余情况在网关后追加 <c>/&lt;action&gt;</c>。
    /// 例：<c>https://gw.example.com/api/pay</c> + query → <c>https://gw.example.com/api/query</c>。
    /// </summary>
    private static string DeriveEndpointUrl(string configured, string gateway, string action)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        var g = (gateway ?? "").Trim().TrimEnd('/');
        if (g.Length == 0) return "";
        // 去掉末尾动作词（pay / pay.do）再拼目标动作，避免出现 /pay/query 这类不存在的路径
        foreach (var suffix in new[] { "/pay.do", "/pay" })
        {
            if (g.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return g[..^suffix.Length] + "/" + action;
        }
        return g + "/" + action;
    }

    /// <summary>
    /// 银行卡通道接线自检：用假订单号构造与**下单完全同构**的签名请求，POST 到收单网关，
    /// 观察网关是否报「签名错误」——不依赖真实订单，也不产生任何真实资金。
    ///
    /// 与支付宝/微信自检同口径：
    ///   ① 通用网关没有统一的查询协议，这里不假设有 query 接口，直接把同构的签名参数
    ///      （商户号 / 假订单号 / 金额 / 时间戳 + HMAC 签名）打到网关，只要能观测到
    ///      「签名是否被接受」即可——业务错误（如订单已存在、参数校验失败）恰恰说明签名已通过；
    ///   ② 回显里只要出现签名错误类关键字（SIGN_ERROR / 签名错误 / 验签失败 …）即判失败；
    ///      其余情况（HTML 网关页、业务错误码）都视为签名已通过，与支付宝自检口径一致；
    ///   ③ 因网关报文格式不统一，自检通过后仍建议用一笔小额真实交易做最终入账验证，
    ///      这一点会写进 <see cref="ChannelSelfCheckDto.Message"/>，避免过度承诺。
    /// </summary>
    public async Task<ChannelSelfCheckDto> SelfCheckAsync()
    {
        var (rt, _) = await ResolveAsync();
        var enabled = (await _payConfig.GetRawAsync())
            .GetValueOrDefault(PayConfigService.KeyBankEnabled, "").Trim() is "1" or "true" or "True";

        var dto = new ChannelSelfCheckDto
        {
            Channel = "bank",
            ChannelName = "银行卡支付",
            Enabled = enabled,
            Configured = rt.CredentialsComplete,
            MerchantId = rt.MerchantNo,
            Gateway = rt.Gateway,
            KeyFormat = "HMAC-SHA256"
        };

        if (!rt.CredentialsComplete)
        {
            dto.Message = "银行卡支付凭证不完整：商户号、收单网关地址、签名密钥 三项缺一不可，请补全后再自检。";
            return dto;
        }
        if (!enabled)
        {
            dto.Message = "银行卡支付开关未启用，自检跳过联网探测。如需验证配置，请先打开「启用银行卡支付」。";
            return dto;
        }

        // 与 CreatePayAsync 同构：参数按 key 字典序拼串后 HMAC-SHA256 签名，只把订单号换成假号
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["merchant_no"] = rt.MerchantNo,
            ["out_trade_no"] = "__SELFCHECK__",
            ["subject"] = "支付通道自检探测",
            ["total_amount"] = "0.01",
            ["currency"] = "CNY",
            ["notify_url"] = rt.NotifyUrl,
            ["return_url"] = rt.ReturnUrl,
            ["timestamp"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString()
        };
        parameters["sign"] = BuildSign(parameters, rt.SignKey);

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var target = rt.Gateway.TrimEnd('/') + (rt.Gateway.Contains('?') ? "&" : "?") + "selfcheck=1";
            var resp = await http.PostAsync(target,
                new FormUrlEncodedContent(parameters.ToDictionary(kv => kv.Key, kv => kv.Value)));
            var body = await resp.Content.ReadAsStringAsync();
            // 先脱敏（清掉签名密钥字面值）再截断：网关回显可能把请求参数原样带回
            dto.RawResponse = SecretMasker.Truncate(SecretMasker.Scrub(body, rt.SignKey), 800);

            var lower = body.ToLowerInvariant();
            var signRejected =
                lower.Contains("sign_error") || lower.Contains("signerror") || lower.Contains("sign error")
                || lower.Contains("invalidsign") || lower.Contains("invalid_sign") || lower.Contains("sign_invalid")
                || body.Contains("签名错误") || body.Contains("验签失败") || body.Contains("签名失败")
                || body.Contains("签名无效") || body.Contains("验签不通过");

            if (signRejected)
            {
                dto.SignatureAccepted = false;
                dto.Message = "收单网关拒绝请求：签名密钥不配对或报文构造不符。请核对签名密钥与收单网关地址是否来自同一家机构。";
            }
            else
            {
                dto.SignatureAccepted = true;
                dto.Message = "收单网关已响应且未报签名错误，签名配置可用（因通用网关无统一查询协议，" +
                              "建议再用一笔小额真实交易最终确认入账无误）。";
            }
            return dto;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("银行卡自检请求异常：{ExType}", ex.GetType().Name);
            dto.Message = ex is TaskCanceledException or OperationCanceledException
                ? "自检请求超时：网关在 15 秒内未响应，请检查网络或网关地址。"
                : $"自检请求异常（{ex.GetType().Name}），请检查网络与网关地址是否正确。";
            return dto;
        }
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
    /// 主动查询银行卡订单状态（前端轮询兜底 / 结算收款窗轮询）。
    ///
    /// 与微信同名方法同口径：查到了成功状态就**当场入账**（复用
    /// <see cref="AlipayService.ApplyTradeResultAsync"/> 的幂等 + 金额校验），
    /// 入账成功返回「已支付」，其余情况返回空串（表示"本次未查到结论，按本地状态走"）。
    ///
    /// 为什么要空串而不是抛异常：调用点（支付页轮询、结算收款窗）对"查不到"的处理
    /// 都是继续用本地状态，抛异常会打断整轮轮询、把一次网络抖动放大成页面报错。
    /// </summary>
    public async Task<string> QueryTradeStatusAsync(string orderNo)
    {
        if (string.IsNullOrWhiteSpace(orderNo)) return "";

        var (rt, real) = await ResolveAsync();
        // 模拟模式没有真实交易可查；未配置查单地址时同样无从查起
        if (!real || string.IsNullOrWhiteSpace(rt.QueryUrl)) return "";

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["merchant_no"] = rt.MerchantNo,
            ["out_trade_no"] = orderNo,
            ["timestamp"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString()
        };
        parameters["sign"] = BuildSign(parameters, rt.SignKey);

        var (ok, body) = await PostAsync(rt.QueryUrl, parameters, "查单");
        if (!ok) return "";

        var (status, amount, tradeNo) = ParseQueryResponse(body);
        if (!IsPaidStatus(status))
            return "";

        // 金额是入账校验的硬条件：网关没回金额就宁可继续用本地状态，
        // 也不能编一个金额送进校验（那会把真实已付款判成「金额不符」而拒绝入账）
        if (amount == null)
        {
            _logger.LogWarning("银行卡查单 {OrderNo} 已成功但响应未含金额，跳过入账", orderNo);
            return "";
        }

        var (applied, msg) = await _alipay.ApplyTradeResultAsync(new Dictionary<string, string>
        {
            ["out_trade_no"] = orderNo,
            ["trade_status"] = "TRADE_SUCCESS",
            ["trade_no"] = string.IsNullOrWhiteSpace(tradeNo) ? $"BANK-{orderNo}" : tradeNo,
            ["total_amount"] = amount.Value.ToString("0.00")
        }, "银行卡");
        _logger.LogInformation("银行卡查单入账 {OrderNo}：{Result}（{Message}）", orderNo, applied ? "成功" : "拒绝", msg);
        return applied ? "已支付" : "";
    }

    /// <summary>
    /// 查询银行卡收单账户余额（管理员看板「支付账户余额」用）。
    ///
    /// 通用网关没有统一余额接口，本方法按最常见约定实现：POST 到 balance 端点，
    /// 带商户号 + 时间戳 + HMAC 签名，响应用宽容解析（JSON / form-urlencoded / 纯文本都试）。
    /// 返回 (是否成功, 可用余额, 总余额, 失败原因)；失败原因只写可读短句，不含凭证。
    /// </summary>
    public async Task<(bool Ok, decimal? Available, decimal? Total, string? Error)> QueryBalanceAsync()
    {
        var (rt, real) = await ResolveAsync();
        if (!rt.CredentialsComplete)
            return (false, null, null, "银行卡余额查询凭证不完整（商户号 / 收单网关 / 签名密钥）");
        if (!real)
            return (false, null, null, "银行卡支付开关未启用");
        if (string.IsNullOrWhiteSpace(rt.BalanceUrl))
            return (false, null, null, "未配置银行卡余额查询地址（可由支付网关自动推导）");

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["merchant_no"] = rt.MerchantNo,
            ["timestamp"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString()
        };
        parameters["sign"] = BuildSign(parameters, rt.SignKey);

        var (ok, body) = await PostAsync(rt.BalanceUrl, parameters, "余额查询");
        if (!ok) return (false, null, null, "余额查询被网关拒绝（请核对签名密钥与余额地址）");

        var (available, total) = ParseBalanceResponse(body);
        if (available == null && total == null)
            return (false, null, null, "网关响应中未找到余额字段（可在配置中改用该机构的余额地址）");
        return (true, available ?? total, total, null);
    }

    /// <summary>签名 POST（查单 / 余额共用）：返回 (是否 2xx, 响应体)。异常不外抛，只记类型。</summary>
    private async Task<(bool Ok, string Body)> PostAsync(
        string url, SortedDictionary<string, string> parameters, string operation)
    {
        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var resp = await http.PostAsync(url,
                new FormUrlEncodedContent(parameters.ToDictionary(kv => kv.Key, kv => kv.Value)));
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("银行卡{Operation}失败：HTTP {Status}", operation, (int)resp.StatusCode);
                return (false, "");
            }
            return (true, body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("银行卡{Operation}请求异常（{ExType}）", operation, ex.GetType().Name);
            return (false, "");
        }
    }

    /// <summary>
    /// 解析查单响应：先按 JSON 递归找字段，失败再按 <c>k=v&amp;</c> 解析（很多网关回表单串）。
    /// 只认明确的成功状态词，其余一律不算已付款。
    /// </summary>
    private static (string Status, decimal? Amount, string TradeNo) ParseQueryResponse(string body)
    {
        var text = (body ?? "").Trim();
        if (text.Length == 0) return ("", null, "");

        var map = FlattenBody(text);
        string? Pick(params string[] names) => names
            .Select(n => map.TryGetValue(n, out var v) ? v : null)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var status = Pick("trade_status", "trade_state", "order_status", "status", "pay_status", "result") ?? "";
        var tradeNo = Pick("trade_no", "transaction_id", "bank_trade_no", "gateway_trade_no", "pay_no") ?? "";
        var amountText = Pick("total_amount", "pay_amount", "trade_amount", "amount", "real_amount");
        decimal? amount = null;
        if (!string.IsNullOrWhiteSpace(amountText) &&
            decimal.TryParse(amountText, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var av))
            amount = av;
        return (status, amount, tradeNo);
    }

    /// <summary>成功状态词表：覆盖常见聚合网关的取值（大小写不敏感，另含中文「成功」）。</summary>
    private static bool IsPaidStatus(string status)
    {
        var s = (status ?? "").Trim().ToUpperInvariant();
        return s is "SUCCESS" or "SUCCEED" or "PAID" or "TRADE_SUCCESS" or "PAY_SUCCESS"
            or "TRADE_FINISHED" or "FINISHED" or "PAYED" or "OK"
            || s.Contains("成功");
    }

    /// <summary>解析余额响应：available/balance/total/amount，带 fen 后缀的按分换算成元。</summary>
    private static (decimal? Available, decimal? Total) ParseBalanceResponse(string body)
    {
        var map = FlattenBody((body ?? "").Trim());
        decimal? Num(string key, bool isFen)
        {
            if (!map.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return null;
            if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)) return null;
            return isFen ? Math.Round(v / 100m, 2) : v;
        }

        var available = Num("available_amount", false) ?? Num("available_balance", false)
                     ?? Num("usable_amount", false) ?? Num("balance", false)
                     ?? Num("available_fen", true) ?? Num("balance_fen", true) ?? Num("amount", false);
        var total = Num("total_amount", false) ?? Num("total_balance", false)
                 ?? Num("account_balance", false) ?? available;
        return (available, total);
    }

    /// <summary>
    /// 把 JSON / form-urlencoded / 单行文本压平成「字段名 → 值」字典（字段名忽略大小写）。
    /// 递归展开嵌套对象（网关常把业务字段包在 data / result 里）。
    /// </summary>
    private static Dictionary<string, string> FlattenBody(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (text.Length == 0) return map;

        if (text.StartsWith('{') || text.StartsWith('['))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                FlattenJson(doc.RootElement, map);
                return map;
            }
            catch (System.Text.Json.JsonException)
            {
                // 不是合法 JSON（XML / 纯文本），继续往下按 k=v 试
            }
        }

        // form-urlencoded：a=1&b=2；也容忍换行分隔
        foreach (var pair in text.Split('&', '\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = pair[..eq].Trim();
            var value = Uri.UnescapeDataString(pair[(eq + 1)..].Trim());
            if (key.Length > 0) map[key] = value;
        }
        return map;
    }

    private static void FlattenJson(System.Text.Json.JsonElement el, Dictionary<string, string> map)
    {
        if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                var kind = p.Value.ValueKind;
                if (kind is System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Array)
                {
                    FlattenJson(p.Value, map);
                    continue;
                }
                var value = kind == System.Text.Json.JsonValueKind.String ? (p.Value.GetString() ?? "")
                          : kind == System.Text.Json.JsonValueKind.Null ? ""
                          : p.Value.GetRawText();
                map.TryAdd(p.Name, value);
            }
        }
        else if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray()) FlattenJson(item, map);
        }
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

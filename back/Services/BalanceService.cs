using System.Globalization;
using System.Text;
using System.Text.Json;
using ERental.Data;
using ERental.DTOs;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>
/// 支付渠道**余额查询**服务：对接支付宝 / 微信支付官方接口，取企业账户可用余额。
///
/// ── 方案选择（为何是"手动触发 + 短缓存 + 失败兜底"，而非定时任务/对账文件）──
/// 管理员看余额是**低频、主动**行为，不需要秒级实时性。四个候选方案中：
///   A 实时查询   —— 每次点开都打官方接口，容易触发限流，接口一挂页面就空白；
///   B 定时任务   —— 页面秒开，但无人访问时白白请求，且需常驻调度；
///   C 手动+缓存  —— **本项目采用**：管理员点了才请求，缓存兜住重复点击，天然限流；
///   D 对账文件   —— 最权威，但 T+1 才有，且拿不到"当前可用余额"，与需求不符。
/// 采用 C，同时吸收 A（提供 force 强制刷新）与 D（并列展示本地账目做粗对账）的优点。
///
/// ── 四项工程保障（需求明确要求）──
///   ① 超时控制：单次请求 8 秒超时，超时即视为该次尝试失败；
///   ② 失败重试：最多 2 次重试（共 3 次尝试），指数退避 300ms → 600ms；
///   ③ 降级兜底：重试仍失败 → 返回上一次成功的缓存值（标记 source=fallback），
///      连缓存都没有才返回 error，**绝不把异常抛给前端**；
///   ④ 结果缓存：成功结果缓存 5 分钟（见 <see cref="CacheMinutes"/>），
///      期间重复请求直接命中缓存，不打官方接口。
///
/// ── 安全约束 ──
/// 本服务只用凭证**发请求**，不打印、不返回、不缓存任何凭证。
/// 官方响应的原始报文体若要记日志，先过 <see cref="SecretMasker.Truncate"/> 截断。
/// 余额数字属业务数据（非密钥），可正常回显给管理员。
/// </summary>
public class BalanceService(
    AppDbContext db, PayConfigService payConfig,
    ILogger<BalanceService> logger, IHttpClientFactory httpFactory)
{
    private readonly AppDbContext _db = db;
    private readonly PayConfigService _payConfig = payConfig;
    private readonly ILogger<BalanceService> _logger = logger;
    private readonly IHttpClientFactory _httpFactory = httpFactory;

    /// <summary>
    /// 余额缓存时长（分钟）。选 5 分钟的依据：
    ///  - 管理员对账场景下，5 分钟前的余额与此刻并无实质差别；
    ///  - 支付宝/微信对账类接口有每日调用上限，5 分钟粒度足以把"反复点开"的请求压到个位数；
    ///  - 需要最新值时可用 force=true 强制刷新，不会因为缓存而看不到变更。
    /// </summary>
    public const int CacheMinutes = 5;

    /// <summary>单次官方接口请求超时（秒）。对账类接口正常响应在 1 秒内，8 秒已很宽松。</summary>
    private const int RequestTimeoutSeconds = 8;

    /// <summary>失败后的额外重试次数（不含首次）。共 3 次尝试，覆盖"偶发网络抖动"。</summary>
    private const int MaxRetries = 2;

    /// <summary>
    /// 失败负缓存时长（分钟）。为什么连"失败"也要缓存：
    /// 查询失败往往是**持续性**的（凭证没权限 / 参数不对 / 账户未开通该产品），
    /// 若失败不进缓存，管理员每点开一次面板就打 3 次官方接口 ——
    /// 比成功时还频繁，恰与"避免频繁请求官方接口"背道而驰。
    /// 取 1 分钟：足以吸住"反复点开/切换折叠"的连点，又不至于让管理员修好配置后还要干等。
    /// </summary>
    private const int FailureCacheMinutes = 1;

    /// <summary>重试的基准退避（毫秒），第 n 次重试等待 base * 2^(n-1)。</summary>
    private const int RetryBaseDelayMs = 300;

    /// <summary>
    /// 进程内缓存：渠道 → (余额, 抓取时刻)。
    /// 为什么用内存而不是落库：余额是易失的展示型数据，落库要处理过期清理、并发写，
    /// 而进程重启后重新查一次的代价很低（且管理员点开时正好会刷新）。
    /// 单实例部署下内存缓存即等价；若将来多实例，再换成分布式缓存即可（本类已把读写收敛到两处）。
    /// </summary>
    private static readonly Dictionary<string, (BalanceDto Value, DateTime At)> Cache = new();

    /// <summary>缓存与重试状态会跨请求读写，用锁保护。</summary>
    private static readonly Lock CacheLock = new();

    /// <summary>清空缓存（保存凭证后调用：凭证变了，旧余额缓存已不代表同一账户）。</summary>
    public static void InvalidateCache()
    {
        lock (CacheLock) Cache.Clear();
    }

    /// <summary>
    /// 取两个渠道的余额 + 本地账目对照。
    /// </summary>
    /// <param name="force">true = 跳过缓存强制刷新（管理员点「刷新」时用）</param>
    public async Task<BalanceOverviewDto> GetOverviewAsync(bool force = false)
    {
        var alipay = await GetAlipayBalanceAsync(force);
        var wechat = await GetWechatBalanceAsync(force);

        var paid = await _db.PaymentRecords.AsNoTracking()
            .Where(p => p.PayStatus == "已支付")
            .SumAsync(p => (decimal?)p.PayAmount) ?? 0m;

        var unpaid = await _db.RentalOrders.AsNoTracking()
            .Where(o => o.OrderStatus == "待支付")
            .SumAsync(o => (decimal?)(o.TotalRent + o.Deposit)) ?? 0m;

        return new BalanceOverviewDto
        {
            Items = [alipay, wechat],
            LocalPaidTotal = paid,
            LocalUnpaidTotal = unpaid,
            CacheMinutes = CacheMinutes,
            Forced = force
        };
    }

    // ══════════════════════════════════════════════════════
    //  支付宝：alipay.fund.account.query（账户余额查询）
    // ══════════════════════════════════════════════════════

    private async Task<BalanceDto> GetAlipayBalanceAsync(bool force)
    {
        const string channel = "alipay";
        var dto = NewDto(channel, "企业支付宝");

        var rt = await _payConfig.ResolveAsync();
        if (rt.Mode == PayConfigService.ModeMock || !rt.CredentialsComplete)
        {
            dto.Status = "not_configured";
            dto.Message = "当前为模拟模式或凭证不全，无法查询官方余额。请先在下方配置生产/沙箱凭证。";
            return dto;
        }

        // PID 是 alipay.fund.account.query 的必填项（官方要求等于请求方 PID）。
        // 缺了就必然报"必填参数为空"，提前拦下可省掉 3 次无效请求，也把原因说清楚。
        if (string.IsNullOrWhiteSpace(rt.Pid))
        {
            dto.Status = "not_configured";
            dto.Message = "尚未填写支付宝会员 ID（PID），无法查询账户余额。PID 为 16 位数字，" +
                          "可在支付宝开放平台「账号中心」查看，需与收款账号一致。";
            return dto;
        }

        if (!force && TryReadCache(channel, out var cached))
        {
            return cached;
        }

        const string path = "alipay.fund.account.query";
        string? lastError = null;
        var retries = 0;

        // 首次 + MaxRetries 次重试
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                retries = attempt;
                // 指数退避：300ms → 600ms，给偶发抖动一点恢复时间，避免立刻再撞
                await Task.Delay(RetryBaseDelayMs * (1 << (attempt - 1)));
            }

            var (ok, amount, total, frozen, err) = await TryAlipayOnceAsync(rt, path);
            if (ok)
            {
                dto.Status = "ok";
                dto.AvailableAmount = amount;
                dto.TotalAmount = total;
                dto.FrozenAmount = frozen;
                dto.Source = "remote";
                dto.FetchedAt = DateTime.Now;
                dto.RetryCount = retries;
                dto.Message = $"查询成功（本次重试 {retries} 次）";
                WriteCache(channel, dto);
                return dto;
            }
            lastError = err;
        }

        // 降级：重试耗尽后回退到缓存的旧值（哪怕已过期），比给管理员一个空白页有用
        return Fallback(channel, dto, retries, lastError);
    }

    /// <summary>发一次支付宝余额查询。</summary>
    private async Task<(bool Ok, decimal? Amount, decimal? Total, decimal? Frozen, string? Error)>
        TryAlipayOnceAsync(PayRuntime rt, string method)
    {
        try
        {
            var parameters = new Dictionary<string, string?>
            {
                ["app_id"] = rt.AppId,
                ["method"] = method,
                ["format"] = "JSON",
                ["charset"] = "utf-8",
                ["sign_type"] = "RSA2",
                ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["version"] = "1.0",
                // alipay.fund.account.query 的业务参数（依官方文档）：
                //   account_type = ACCTRANS_ACCOUNT  查询的账号类型，"余额户"即此值，**必填**；
                //   alipay_user_id                    支付宝会员 ID，必须等于请求发起方的 PID，
                //                                     缺了会报"必填参数为空"；
                // ⚠️ account_product_code / account_scene_code 只在查**托管子户**时使用，
                //    且官方明确"开户场景码与开户产品码不可同时传递"——
                //    之前这里两个都传，才被判 INVALID_PARAMETER 取值不合法。
                ["biz_content"] = $"{{\"account_type\":\"ACCTRANS_ACCOUNT\"," +
                                  $"\"alipay_user_id\":\"{rt.Pid}\"}}"
            };
            parameters["sign"] = AlipaySigner.Sign(AlipaySigner.BuildSignContent(parameters), rt.PrivateKey);

            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds);
            var resp = await http.PostAsync(rt.Gateway.TrimEnd('?') + "?charset=utf-8",
                new FormUrlEncodedContent(parameters
                    .Where(kv => kv.Value != null)
                    .ToDictionary(kv => kv.Key, kv => kv.Value!)));
            var body = await ReadBodyAsync(resp);

            // 签名被拒 → 凭证问题，重试也没意义，直接把原因作为错误返回
            if (body.Contains("isv.invalid-signature"))
                return (false, null, null, null, "签名被拒（应用私钥与平台登记的公钥不配对）");

            // 取响应里的可用余额字段。支付宝返回形如 "available_amount":"1234.56"
            var amount = ExtractDecimal(body, "available_amount");
            var total = ExtractDecimal(body, "total_amount");
            if (amount == null && total == null)
            {
                // 没取到金额字段：可能是业务错误码（如无权限查余额）
                return (false, null, null, null, SummarizeAlipayError(body));
            }
            return (true, amount ?? total, total, null, null);
        }
        catch (TaskCanceledException)
        {
            return (false, null, null, null, "请求超时");
        }
        catch (Exception ex)
        {
            return (false, null, null, null, ex.GetType().Name);
        }
    }

    /// <summary>从支付宝响应里抽取错误码/错误信息，转成给管理员看的短句（不外泄原始报文）。</summary>
    private static string SummarizeAlipayError(string body)
    {
        var code = ExtractString(body, "sub_code") ?? ExtractString(body, "code");
        var msg = ExtractString(body, "sub_msg") ?? ExtractString(body, "msg");
        if (code == null && msg == null) return "官方接口返回了非预期内容";
        return $"官方接口返回：{code ?? "未知错误"} {msg ?? ""}".Trim();
    }

    // ══════════════════════════════════════════════════════
    //  微信支付：GET /v3/merchant/fund/balance/{account_type}
    // ══════════════════════════════════════════════════════

    private static Task<BalanceDto> GetWechatBalanceAsync(bool force)
    {
        _ = force;   // 占位通道暂不支持强制刷新；保留参数以对齐支付宝分支的签名
        const string channel = "wechat";
        var dto = NewDto(channel, "微信支付商户");

        // 微信为占位通道：尚无 V3 签名实现，明确告知"未接入"而非"查询失败"，
        // 避免管理员以为是自己填错了配置而反复排查。
        dto.Status = "not_integrated";
        dto.Message = "微信支付为占位通道（未接入真实 V3 接口），暂无商户余额。接入后此处显示商户可用余额。";
        dto.Source = "none";
        return Task.FromResult(dto);
    }

    // ══════════════════════════════════════════════════════
    //  通用：缓存、降级、解析
    // ══════════════════════════════════════════════════════

    private static BalanceDto NewDto(string channel, string name) => new()
    {
        Channel = channel,
        ChannelName = name,
        Currency = "CNY",
        Source = "none"
    };

    /// <summary>
    /// 读缓存。成功的缓存按 <see cref="CacheMinutes"/> 计龄；
    /// 失败结果按 <see cref="FailureCacheMinutes"/> 计龄，且命中也只回"上次失败原因"，
    /// 目的是**压住重复请求**而不是冒充成功数据（前端据 status 照常显示失败态）。
    /// </summary>
    private static bool TryReadCache(string channel, out BalanceDto dto)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(channel, out var entry))
            {
                var ok = entry.Value.Status == "ok";
                var ttlAll = (ok ? CacheMinutes : FailureCacheMinutes) * 60;
                var ttl = ttlAll - (int)(DateTime.Now - entry.At).TotalSeconds;
                if (ttl > 0)
                {
                    // 从缓存复制一份再改标记，避免把 source 改到缓存本体上
                    var hit = Clone(entry.Value);
                    hit.Source = "cache";
                    hit.CacheTtlSeconds = ttl;
                    hit.Message = ok
                        ? $"缓存命中（{CacheMinutes} 分钟内不重复请求官方接口，剩余 {ttl} 秒）"
                        : $"{hit.Message}（失败结果 {FailureCacheMinutes} 分钟内不重复请求，剩余 {ttl} 秒）";
                    dto = hit;
                    return true;
                }
            }
        }
        dto = null!;
        return false;
    }

    private static void WriteCache(string channel, BalanceDto value)
    {
        lock (CacheLock)
        {
            Cache[channel] = (Clone(value), DateTime.Now);
        }
    }

    /// <summary>
    /// 降级兜底：优先回上一次成功的值（哪怕已过期），否则回一个明确的失败说明。
    /// 关键：**不抛异常、不返回空白**——管理员至少能看到"上次是多少 + 这次为什么没查到"。
    /// </summary>
    private BalanceDto Fallback(string channel, BalanceDto fresh, int retries, string? error)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(channel, out var entry) && entry.Value.Status == "ok")
            {
                var old = Clone(entry.Value);
                old.Status = "ok";
                old.Source = "fallback";
                old.RetryCount = retries;
                old.CacheTtlSeconds = 0;
                old.FetchedAt = entry.At;
                old.Message = $"官方接口暂时不可用（{error ?? "未知原因"}，已重试 {retries} 次），" +
                              $"显示的是 {entry.At:HH:mm:ss} 的数据";
                return old;
            }
        }

        fresh.Status = error == "请求超时" ? "timeout" : "error";
        fresh.Source = "none";
        fresh.RetryCount = retries;
        fresh.FetchedAt = DateTime.Now;
        fresh.Message = $"查询失败：{error ?? "未知原因"}（已重试 {retries} 次）";
        // 失败也写进缓存（负缓存）：否则每次展开面板都重打 3 次官方接口，
        // 比成功时还频繁。force=true 仍会跳过缓存，管理员修好凭证后点「刷新余额」即可立即重试。
        WriteCache(channel, fresh);
        _logger.LogWarning("余额查询最终失败（渠道 {Channel}，重试 {Retries} 次，原因 {Reason}）",
            channel, retries, error);
        return fresh;
    }

    private static BalanceDto Clone(BalanceDto s) => new()
    {
        Channel = s.Channel,
        ChannelName = s.ChannelName,
        Status = s.Status,
        AvailableAmount = s.AvailableAmount,
        TotalAmount = s.TotalAmount,
        FrozenAmount = s.FrozenAmount,
        Currency = s.Currency,
        Source = s.Source,
        FetchedAt = s.FetchedAt,
        CacheTtlSeconds = s.CacheTtlSeconds,
        RetryCount = s.RetryCount,
        Message = s.Message
    };

    /// <summary>用 System.Text.Json 精确取字段，避免"字符串包含"式的误判。</summary>
    private static decimal? ExtractDecimal(string json, string field)
    {
        var s = ExtractString(json, field);
        return s != null && decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string? ExtractString(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FindString(doc.RootElement, field);
        }
        catch
        {
            // 支付宝响应不是严格 JSON（偶有 GBK 杂质）时退化为简单匹配
            var idx = json.IndexOf($"\"{field}\"", StringComparison.Ordinal);
            if (idx < 0) return null;
            var colon = json.IndexOf(':', idx);
            if (colon < 0) return null;
            var start = json.IndexOf('"', colon + 1);
            if (start < 0) return null;
            var end = json.IndexOf('"', start + 1);
            return end < 0 ? null : json[(start + 1)..end];
        }
    }

    /// <summary>递归找字段（支付宝把业务字段包在 alipay_fund_account_query_response 里）。</summary>
    private static string? FindString(JsonElement el, string field)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                if (string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase))
                    return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
                var nested = FindString(p.Value, field);
                if (nested != null) return nested;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var nested = FindString(item, field);
                if (nested != null) return nested;
            }
        }
        return null;
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage resp)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        var charset = resp.Content.Headers.ContentType?.CharSet?.Trim('"', ' ');
        if (!string.IsNullOrEmpty(charset))
        {
            try { return Encoding.GetEncoding(charset).GetString(bytes); }
            catch { /* 编码名不支持，走兜底 */ }
        }
        try { return Encoding.GetEncoding("GBK").GetString(bytes); }
        catch { return Encoding.UTF8.GetString(bytes); }
    }
}

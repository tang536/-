using System.Globalization;
using System.Text;
using System.Text.Json;
using ERental.Data;
using ERental.DTOs;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

///<summary>支付渠道余额查询服务：对接支付宝/微信支付官方接口，取企业账户可用余额</summary>
public class BalanceService(AppDbContext db, PayConfigService payConfig, WechatPayService wechat, BankPayService bank, ILogger<BalanceService> logger, IHttpClientFactory httpFactory)
{
    ///<summary>余额查询状态：通道未配置</summary>
    private const string StatusNotConfigured = "not_configured";

    private readonly AppDbContext _db = db;
    private readonly PayConfigService _payConfig = payConfig;
    private readonly WechatPayService _wechat = wechat;
    private readonly BankPayService _bank = bank;
    private readonly ILogger<BalanceService> _logger = logger;
    private readonly IHttpClientFactory _httpFactory = httpFactory;

    ///<summary>余额缓存时长（分钟）</summary>
    public const int CacheMinutes = 5;

    ///<summary>单次官方接口请求超时（秒）</summary>
    private const int RequestTimeoutSeconds = 8;

    ///<summary>失败后的额外重试次数（不含首次）</summary>
    private const int MaxRetries = 2;

    ///<summary>失败负缓存时长（分钟）</summary>
    private const int FailureCacheMinutes = 1;

    ///<summary>重试的基准退避（毫秒），第 n 次重试等待 base*2^(n-1)</summary>
    private const int RetryBaseDelayMs = 300;

    ///<summary>进程内缓存：渠道 → (余额,抓取时刻,抓取时的单调计时)</summary>
    private static readonly Dictionary<string, (BalanceDto Value, DateTime At, long Ticks)> Cache = new();

    ///<summary>缓存与重试状态会跨请求读写，用锁保护</summary>
    private static readonly Lock CacheLock = new();

    ///<summary>清空缓存（保存凭证后调用：凭证变了，旧余额缓存已不代表同一账户）</summary>
    public static void InvalidateCache()
    {
        lock (CacheLock) Cache.Clear();
    }

    ///<summary>取三个渠道（支付宝/微信/银行卡）的余额+本地账目对照 三个渠道必须串行查询</summary>
    public async Task<BalanceOverviewDto> GetOverviewAsync(bool force = false)
    {
        var items = new List<BalanceDto>
        {
            await GetAlipayBalanceAsync(force),
            await GetWechatBalanceAsync(force),
            await GetBankBalanceAsync(force)
        };

        //本地实收=钱到过账的全部支付
        var paid = await _db.PaymentRecords.AsNoTracking().Where(p => PayStatuses.MoneyArrived.Contains(p.PayStatus)).SumAsync(p => (decimal?)p.PayAmount) ?? 0m;

        var unpaid = await _db.RentalOrders.AsNoTracking().Where(o => o.OrderStatus == "待支付").SumAsync(o => (decimal?)(o.TotalRent + o.Deposit)) ?? 0m;

        return new BalanceOverviewDto
        {
            Items = [.. items],
            LocalPaidTotal = paid,
            LocalUnpaidTotal = unpaid,
            CacheMinutes = CacheMinutes,
            Forced = force
        };
    }

    //══════════════════════════════════════════════════════支付宝：alipay.fund.account.query（账户余额查询）══════════════════════════════════════════════════════

    private async Task<BalanceDto> GetAlipayBalanceAsync(bool force)
    {
        var dto = NewDto("alipay", "企业支付宝");

        var rt = await _payConfig.ResolveAsync();
        if (rt.Mode == PayConfigService.ModeMock || !rt.CredentialsComplete)
        {
            dto.Status = StatusNotConfigured;
            dto.Message = "当前为模拟模式或凭证不全，无法查询官方余额。请先在下方配置生产/沙箱凭证。";
            return dto;
        }

        //PID 是 alipay.fund.account.query 的必填项（官方要求等于请求方 PID）
        if (string.IsNullOrWhiteSpace(rt.Pid))
        {
            dto.Status = StatusNotConfigured;
            dto.Message = "尚未填写支付宝会员 ID（PID），无法查询账户余额。PID 为 16 位数字，" + "可在支付宝开放平台「账号中心」查看，需与收款账号一致。";
            return dto;
        }

        return await QueryWithRetryAsync(dto, force, async () =>
        {
            var (ok, amount, total, frozen, err) = await TryAlipayOnceAsync(rt, "alipay.fund.account.query");
            return (ok, amount, total, frozen, err);
        });
    }

    ///<summary>发一次支付宝余额查询</summary>
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
                //alipay.fund.account.query 的业务参数（依官方文档）：account_type=ACCTRANS_ACCOUNT 查询的账号类型
                ["biz_content"] = $"{{\"account_type\":\"ACCTRANS_ACCOUNT\"," + $"\"alipay_user_id\":\"{rt.Pid}\"}}"
            };
            parameters["sign"] = AlipaySigner.Sign(AlipaySigner.BuildSignContent(parameters), rt.PrivateKey);

            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds);
            var resp = await http.PostAsync(rt.Gateway.TrimEnd('?') + "?charset=utf-8", new FormUrlEncodedContent(parameters.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value!)));
            var body = await ReadBodyAsync(resp);

            //签名被拒 → 凭证问题，重试也没意义，直接把原因作为错误返回
            if (body.Contains("isv.invalid-signature")) return (false, null, null, null, "签名被拒（应用私钥与平台登记的公钥不配对）");

            //取响应里的可用余额字段
            var amount = ExtractDecimal(body, "available_amount");
            var total = ExtractDecimal(body, "total_amount");
            if (amount == null && total == null)
            {
                //没取到金额字段：可能是业务错误码（如无权限查余额）
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

    ///<summary>从支付宝响应里抽取错误码/错误信息</summary>
    private static string SummarizeAlipayError(string body)
    {
        var code = ExtractString(body, "sub_code") ?? ExtractString(body, "code");
        var msg = ExtractString(body, "sub_msg") ?? ExtractString(body, "msg");
        if (code == null && msg == null) return "官方接口返回了非预期内容";
        return $"官方接口返回：{code ?? "未知错误"} {msg ?? ""}".Trim();
    }

    //══════════════════════════════════════════════════════微信支付：GET /v3/merchant/fund/balance/{account_type}══════════════════════════════════════════════════════

    private async Task<BalanceDto> GetWechatBalanceAsync(bool force)
    {
        var dto = NewDto("wechat", "微信支付商户");

        //预检放在重试之外
        var (rt, _) = await _wechat.ResolveAsync();
        if (!rt.Enabled)
        {
            dto.Status = StatusNotConfigured;
            dto.Message = "微信支付开关未启用，无法查询商户余额。如需查看余额，请先在下方打开「启用微信支付」并保存凭证。";
            return dto;
        }
        if (!rt.BalanceCredentialsComplete)
        {
            dto.Status = StatusNotConfigured;
            dto.Message = "微信支付余额查询凭证不完整（商户号 / 证书序列号 / 商户私钥），无法查询商户余额。";
            return dto;
        }

        return await QueryWithRetryAsync(dto, force, async () =>
        {
            var (ok, available, pending, err) = await _wechat.QueryBalanceAsync();
            //微信只有「可用余额」与「待结算金额」（pending_amount），没有支付宝那种总额字段
            return (ok, available, available, pending, err);
        });
    }

    //══════════════════════════════════════════════════════银行卡：POST{gateway}/balance

    private async Task<BalanceDto> GetBankBalanceAsync(bool force)
    {
        var dto = NewDto("bank", "银行卡收单");

        var (rt, real) = await _bank.ResolveAsync();
        if (!rt.CredentialsComplete)
        {
            dto.Status = StatusNotConfigured;
            dto.Message = "银行卡余额查询凭证不完整（商户号 / 收单网关 / 签名密钥），无法查询收单账户余额。";
            return dto;
        }
        if (!real)
        {
            dto.Status = StatusNotConfigured;
            dto.Message = "银行卡支付开关未启用，无法查询收单账户余额。";
            return dto;
        }

        return await QueryWithRetryAsync(dto, force, async () =>
        {
            var (ok, available, total, err) = await _bank.QueryBalanceAsync();
            return (ok, available, total, null, err);
        });
    }

    //══════════════════════════════════════════════════════通用：缓存

    ///<summary>三通道共用的「缓存</summary>
    private async Task<BalanceDto> QueryWithRetryAsync(
        BalanceDto dto, bool force,
        Func<Task<(bool Ok, decimal? Available, decimal? Total, decimal? Frozen, string? Error)>> probe)
    {
        if (!force && TryReadCache(dto.Channel, out var cached)) return cached;

        string? lastError = null;
        var retries = 0;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                retries = attempt;
                //指数退避
                await Task.Delay(RetryBaseDelayMs * (1 << (attempt - 1)));
            }

            var (ok, available, total, frozen, err) = await probe();
            if (ok)
            {
                dto.Status = "ok";
                dto.AvailableAmount = available;
                dto.TotalAmount = total;
                dto.FrozenAmount = frozen;
                dto.Source = "remote";
                dto.FetchedAt = DateTime.Now;
                dto.RetryCount = retries;
                dto.Message = $"查询成功（本次重试 {retries} 次）";
                WriteCache(dto.Channel, dto);
                return dto;
            }
            lastError = err;
        }

        //降级：重试耗尽后回退到缓存的旧值（哪怕已过期），比给管理员一个空白页有用
        return Fallback(dto.Channel, dto, retries, lastError);
    }

    //══════════════════════════════════════════════════════通用：缓存、降级、解析══════════════════════════════════════════════════════

    private static BalanceDto NewDto(string channel, string name) => new()
    {
        Channel = channel,
        ChannelName = name,
        Currency = "CNY",
        Source = "none"
    };

    ///<summary>读缓存</summary>
    private static bool TryReadCache(string channel, out BalanceDto dto)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(channel, out var entry))
            {
                var ok = entry.Value.Status == "ok";
                var ttlAll = (ok ? CacheMinutes : FailureCacheMinutes) * 60;
                var ttl = ttlAll - (int)((Environment.TickCount64 - entry.Ticks) / 1000);
                if (ttl > 0)
                {
                    //从缓存复制一份再改标记
                    var hit = Clone(entry.Value);
                    hit.Source = "cache";
                    hit.CacheTtlSeconds = ttl;
                    hit.Message = ok ? $"缓存命中（{CacheMinutes} 分钟内不重复请求官方接口，剩余 {ttl} 秒）" : $"{hit.Message}（失败结果 {FailureCacheMinutes} 分钟内不重复请求，剩余 {ttl} 秒）";
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
            Cache[channel] = (Clone(value), DateTime.Now, Environment.TickCount64);
        }
    }

    ///<summary>降级兜底</summary>
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
                old.Message = $"官方接口暂时不可用（{error ?? "未知原因"}，已重试 {retries} 次），" + $"显示的是 {entry.At:HH:mm:ss} 的数据";
                return old;
            }
        }

        fresh.Status = error == "请求超时" ? "timeout" : "error";
        fresh.Source = "none";
        fresh.RetryCount = retries;
        fresh.FetchedAt = DateTime.Now;
        fresh.Message = $"查询失败：{error ?? "未知原因"}（已重试 {retries} 次）";
        //失败也写进缓存（负缓存）
        WriteCache(channel, fresh);
        _logger.LogWarning("余额查询最终失败（渠道 {Channel}，重试 {Retries} 次，原因 {Reason}）", channel, retries, error);
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

    ///<summary>用 System</summary>
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
            //支付宝响应不是严格 JSON（偶有 GBK 杂质）时退化为简单匹配
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

    ///<summary>递归找字段（支付宝把业务字段包在 alipay_fund_account_query_response 里）</summary>
    private static string? FindString(JsonElement el, string field) => el.ValueKind switch
    {
        JsonValueKind.Object => FindInObject(el, field),
        JsonValueKind.Array => FindInArray(el, field),
        JsonValueKind.Undefined => null,
        JsonValueKind.String => null,
        JsonValueKind.Number => null,
        JsonValueKind.True => null,
        JsonValueKind.False => null,
        JsonValueKind.Null => null,
        _ => null                      //未命名枚举值（JsonValueKind 是开放枚举）同样到底
    };

    ///<summary>对象内查找：先比当前层属性名，再逐个子节点递归</summary>
    private static string? FindInObject(JsonElement el, string field)
    {
        foreach (var p in el.EnumerateObject())
        {
            if (string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase)) return FieldText(p.Value);

            var nested = FindString(p.Value, field);
            if (nested != null) return nested;
        }
        return null;
    }

    ///<summary>数组内查找：按元素顺序取第一个命中的（支付宝偶尔把结果包成列表）</summary>
    private static string? FindInArray(JsonElement el, string field)
    {
        foreach (var item in el.EnumerateArray())
        {
            var nested = FindString(item, field);
            if (nested != null) return nested;
        }
        return null;
    }

    ///<summary>字段取值：字符串直接取原文，其余类型（数字/布尔）走 ToString，保持原有取数口径</summary>
    private static string? FieldText(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();

    private static async Task<string> ReadBodyAsync(HttpResponseMessage resp)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        var charset = resp.Content.Headers.ContentType?.CharSet?.Trim('"', ' ');
        if (!string.IsNullOrEmpty(charset))
        {
            try { return Encoding.GetEncoding(charset).GetString(bytes); }
            catch {/* 编码名不支持，走兜底 */}
        }
        try { return Encoding.GetEncoding("GBK").GetString(bytes); }
        catch { return Encoding.UTF8.GetString(bytes); }
    }
}

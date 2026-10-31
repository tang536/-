using System.Text.RegularExpressions;
using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using ERental.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERental.Services;

/// <summary>支付凭证的存取与「三级自动降级」判定</summary>
public partial class PayConfigService(AppDbContext db, IOptions<LanOptions> lan, SecretProvider secrets)
{
    private readonly AppDbContext _db = db;
    private readonly SecretProvider _secrets = secrets;

    /// <summary>局域网回环地址识别：整段 localhost/127</summary>
    [GeneratedRegex(@"^https?://(localhost|127\.0\.0\.1)(:|/|$)")]
    private static partial Regex LocalHostUrlRegex();

    /// <summary>局域网回环前缀（含可选端口）：用于把历史遗留地址整段重写为本机 IP + 当前端口</summary>
    [GeneratedRegex(@"^https?://(localhost|127\.0\.0\.1)(:\d+)?")]
    private static partial Regex LocalHostPrefixRegex();

    /// <summary>局域网模式下按需覆盖为「本机 IP + 当前前端端口」</summary>
    private readonly LanOptions _lan = lan.Value;

    /// <summary>支付配置项的键前缀</summary>
    public const string Prefix = "pay.";

    // ── 生产环境凭证 ──
    public const string KeyProdEnabled = Prefix + "prod_enabled";
    public const string KeyProdAppId = Prefix + "prod_appid";
    public const string KeyProdPrivateKey = Prefix + "prod_private_key";
    public const string KeyProdGateway = Prefix + "prod_gateway";

    // ── 沙箱环境凭证 ──
    public const string KeySandboxAppId = Prefix + "sandbox_appid";
    public const string KeySandboxPrivateKey = Prefix + "sandbox_private_key";
    public const string KeySandboxGateway = Prefix + "sandbox_gateway";

    // ── 两级共用的支付宝公钥（验签用） ──
    // 支付宝公钥是"支付宝的"公钥，同一 AppId 下生产与沙箱各一把；这里分开存，避免切环境时串用。
    public const string KeyProdAlipayPublicKey = Prefix + "prod_alipay_public_key";
    public const string KeySandboxAlipayPublicKey = Prefix + "sandbox_alipay_public_key";

    // ── 支付宝会员 ID（PID，非密钥） ──
    // 专供 alipay.fund.account.query 查余额：官方要求 alipay_user_id 必须等于请求发起方的 PID。
    // 与 AppId 是两回事：AppId 标识"应用"，PID 标识"收款账号"，一个应用可绑定不同账号。
    private const string KeyProdPid = Prefix + "prod_pid";
    private const string KeySandboxPid = Prefix + "sandbox_pid";

    // ── 回调地址（生产必须公网可达；沙箱开发期用内网穿透） ──
    public const string KeyNotifyUrl = Prefix + "notify_url";
    public const string KeyReturnUrl = Prefix + "return_url";

    // ══════════════════════════════════════════════════════════════════
    // 「微信支付 / 银行卡支付」的配置键。
    //
    // 两条通道均已实现正式通道服务（WechatPayService / BankPayService）：
    //   · 微信 —— APIv3 协议（请求签名 + AES-GCM 回调解密 + 查单兜底）；
    //   · 银行卡 —— 通用收银网关协议（HMAC-SHA256 签名跳转 + 回调验签）。
    // 与支付宝同一套降级思路：**凭证齐全 → 走真实通道；不齐全 → 内置模拟收银台**，
    // 判定条件是各通道自己的必填项，不与支付宝的三级降级互相干扰。
    // ══════════════════════════════════════════════════════════════════

    // ── 微信支付（APIv3） ──
    // cert_serial：**商户 API 证书序列号**（40 位十六进制）。请求签名的 Authorization 头里 serial_no 用它，
    //   填错（例如填成 PUB 开头的微信支付公钥序号）会让本系统发出的每一笔请求都验签失败。
    // platform_key：微信支付公钥 PEM，**只**用于回调验签。回调头里的 Wechat-Serial（PUB 开头）目前仅记录日志、
    //   不参与校验，因此不需要、也不应该把它填进 cert_serial。
    // pay_mode：h5 = 浏览器跳转微信 H5 收银台（仅企业主体，且需 ICP 备案域名、类目审核、经营数据）；
    //   native = 下单返回 code_url 由前端渲染二维码让用户扫码（个体工商户即可开通，无备案与类目要求）。
    public const string KeyWechatEnabled = Prefix + "wechat_enabled";
    public const string KeyWechatAppId = Prefix + "wechat_appid";
    public const string KeyWechatMchId = Prefix + "wechat_mchid";
    public const string KeyWechatApiV3Key = Prefix + "wechat_apiv3_key";
    public const string KeyWechatCertSerial = Prefix + "wechat_cert_serial";
    public const string KeyWechatPrivateKey = Prefix + "wechat_private_key";
    public const string KeyWechatNotifyUrl = Prefix + "wechat_notify_url";
    public const string KeyWechatGateway = Prefix + "wechat_gateway";
    public const string KeyWechatPlatformKey = Prefix + "wechat_platform_key";
    public const string KeyWechatPayMode = Prefix + "wechat_pay_mode";

    /// <summary>微信支付方式：H5 跳转（历史默认）</summary>
    public const string WechatPayModeH5 = "h5";

    /// <summary>微信支付方式：Native 扫码</summary>
    public const string WechatPayModeNative = "native";

    // ── 银行卡支付（通用收银网关） ──
    // 网关协议采用业内通行的「参数签名跳转 + 回调验签」：选定机构后填其网关地址与签名密钥即可。
    public const string KeyBankEnabled = Prefix + "bank_enabled";
    public const string KeyBankMerchantNo = Prefix + "bank_merchant_no";
    public const string KeyBankGateway = Prefix + "bank_gateway";
    public const string KeyBankSignKey = Prefix + "bank_sign_key";
    public const string KeyBankNotifyUrl = Prefix + "bank_notify_url";
    // 退款地址非必填：留空时由支付网关推导（/pay → /refund，其余追加 /refund），
    // 所选机构的路径规范不同时再显式填写。
    public const string KeyBankRefundUrl = Prefix + "bank_refund_url";

    // 查单 / 余额地址同样非必填：通用收银网关没有统一协议，这两条路径的命名各家不同
    // （/query、/order/query、/trade/query …；/balance、/account/balance …）。
    // 留空时按最常见的约定由支付网关推导（/pay → /query、/balance），
    // 与实际机构的路径规范不符时再显式填写，不必改代码。
    public const string KeyBankQueryUrl = Prefix + "bank_query_url";
    public const string KeyBankBalanceUrl = Prefix + "bank_balance_url";

    /// <summary>支付模式：生产 / 沙箱 / 模拟</summary>
    public const string ModeProd = "生产";
    private const string ModeSandbox = "沙箱";
    public const string ModeMock = "模拟";

    /// <summary>把支付方式归一到合法取值</summary>
    public static string NormalizeWechatPayMode(string? value) =>
        string.Equals(value?.Trim(), WechatPayModeNative, StringComparison.OrdinalIgnoreCase)
            ? WechatPayModeNative
            : WechatPayModeH5;

    private const string DefaultProdGateway = "https://openapi.alipay.com/gateway.do";
    private const string DefaultSandboxGateway = "https://openapi-sandbox.dl.alipaydev.com/gateway.do";

    /// <summary>回调地址兜底：管理员没填（或填的仍是 localhost）时，按当前运行环境给一个能用的值</summary>
    private string FallbackLocalUrl(string configured, string path)
    {
        var value = configured.Trim();
        var ip = _lan.Enabled
            ? string.IsNullOrWhiteSpace(_lan.Host) ? NetworkHelper.ResolveLanIp() : _lan.Host.Trim()
            : null;

        // 非局域网模式：原样返回，行为与改造前完全一致
        if (string.IsNullOrWhiteSpace(ip)) return value;

        // 后端自身监听的明文端口（Kestrel 不做 TLS，HTTPS 由 vite / nginx 在外层终止）
        var notifyBase = $"http://{ip}:5105";
        var isNotify = path.Contains("notify");
        // 前端入口：协议由 Lan:FrontScheme 决定，默认 https
        var frontBase = $"{NormalizeFrontScheme(_lan.FrontScheme)}://{ip}:{_lan.CustomerPort}";

        if (string.IsNullOrWhiteSpace(value))
            return isNotify ? $"{notifyBase}/{path}" : $"{frontBase}/{path}";

        // 局域网模式下，历史遗留的 localhost 地址必须整段重写：
        // 只替换主机名的话，端口还停在旧值（如已废弃的 5174），回跳/回调照样打不开。
        // 这里把「localhost / 127.0.0.1 + 任意端口」统一改写为「本机 IP + 当前端口」；
        // 管理员填的真实域名 / 内网 IP（非回环）不受影响——生产回调仍只认人工显式配置。
        if (LocalHostUrlRegex().IsMatch(value))
            return LocalHostPrefixRegex().Replace(value, isNotify ? notifyBase : frontBase);

        return value.Replace("localhost", ip).Replace("127.0.0.1", ip);
    }

    /// <summary>只接受 http / https，其余（含空值、历史脏值）按 https 处理——局域网入口恒为 HTTPS</summary>
    private static string NormalizeFrontScheme(string? scheme) =>
        string.Equals(scheme?.Trim(), "http", StringComparison.OrdinalIgnoreCase) ? "http" : "https";

    /// <summary>回跳地址兜底的公开入口（微信/银行卡通道构建跳转与回调地址用）</summary>
    public string FallbackChannelUrl(string configured, string path) => FallbackLocalUrl(configured, path);

    /// <summary>从请求头还原「用户此刻访问的源」，供调用方拼回跳/通知地址</summary>
    public static string? ResolveClientOrigin(string? explicitOrigin, string? referer)
    {
        if (!string.IsNullOrWhiteSpace(explicitOrigin)) return explicitOrigin.Trim();
        if (string.IsNullOrWhiteSpace(referer)) return null;
        return Uri.TryCreate(referer.Trim(), UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority}"
            : null;
    }

    /// <summary>构建「支付完成后回跳的前端地址」——优先采用用户此刻访问的源</summary>
    public async Task<string> BuildReturnUrlAsync(string? origin, string path)
    {
        var normalized = NormalizeOrigin(origin);
        if (normalized != null) return $"{normalized}/{path.TrimStart('/')}";

        var raw = await GetRawAsync();
        var configured = raw.TryGetValue(KeyReturnUrl, out var v) ? v.Trim() : "";
        return FallbackChannelUrl(configured, path);
    }

    /// <summary>构建「支付平台异步通知地址」——同样优先采用用户当前访问的源</summary>
    public async Task<string> BuildNotifyUrlAsync(string? origin, string path)
    {
        var normalized = NormalizeOrigin(origin);
        if (normalized != null) return $"{normalized}/{path.TrimStart('/')}";

        var raw = await GetRawAsync();
        var configured = raw.TryGetValue(KeyNotifyUrl, out var v) ? v.Trim() : "";
        return FallbackChannelUrl(configured, path);
    }

    /// <summary>校验并规范化浏览器传来的 origin</summary>
    private static string? NormalizeOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return null;
        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>敏感项分层解析的公开入口（env > secrets > db），供各支付服务复用同一套优先级</summary>
    public string Secret(string key, string dbValue) => _secrets.Resolve(key, dbValue, out _);

    /// <summary>读出全部支付配置（键 → 值）</summary>
    public async Task<Dictionary<string, string>> GetRawAsync()
    {
        var rows = await _db.SystemConfigs
            .AsNoTracking()
            .Where(c => c.ConfigKey.StartsWith(Prefix))
            .ToListAsync();

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            dict[r.ConfigKey] = r.ConfigValue ?? string.Empty;
        return dict;
    }

    /// <summary>判定当前生效的支付模式 + 组装出该模式的接入参数</summary>
    public async Task<PayRuntime> ResolveAsync()
    {
        var raw = await GetRawAsync();

        string Get(string key) => raw.GetValueOrDefault(key, "").Trim();
        // 敏感项：按分层优先级取值（env > secrets > db）
        string SecretOf(string key) => _secrets.Resolve(key, Get(key), out _);
        bool ProdSwitchOn() => Get(KeyProdEnabled) is "1" or "true" or "True";

        // ① 生产：开关打开 + 三件套齐全
        if (ProdSwitchOn())
        {
            var appId = Get(KeyProdAppId);
            var priv = SecretOf(KeyProdPrivateKey);
            var aliPub = SecretOf(KeyProdAlipayPublicKey);
            if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(priv) && !string.IsNullOrWhiteSpace(aliPub))
            {
                return new PayRuntime
                {
                    Mode = ModeProd,
                    AppId = appId,
                    PrivateKey = priv,
                    AlipayPublicKey = aliPub,
                    Gateway = string.IsNullOrWhiteSpace(Get(KeyProdGateway)) ? DefaultProdGateway : Get(KeyProdGateway),
                    NotifyUrl = FallbackLocalUrl(Get(KeyNotifyUrl), "api/payment/alipay/notify"),
                    ReturnUrl = FallbackLocalUrl(Get(KeyReturnUrl), "pay-result"),
                    Pid = Get(KeyProdPid)
                };
            }
        }

        // ② 沙箱：三件套齐全
        var sbAppId = Get(KeySandboxAppId);
        var sbPriv = SecretOf(KeySandboxPrivateKey);
        var sbPub = SecretOf(KeySandboxAlipayPublicKey);
        if (!string.IsNullOrWhiteSpace(sbAppId) && !string.IsNullOrWhiteSpace(sbPriv) && !string.IsNullOrWhiteSpace(sbPub))
        {
            return new PayRuntime
            {
                Mode = ModeSandbox,
                AppId = sbAppId,
                PrivateKey = sbPriv,
                AlipayPublicKey = sbPub,
                Gateway = string.IsNullOrWhiteSpace(Get(KeySandboxGateway)) ? DefaultSandboxGateway : Get(KeySandboxGateway),
                NotifyUrl = FallbackLocalUrl(Get(KeyNotifyUrl), "api/payment/alipay/notify"),
                ReturnUrl = FallbackLocalUrl(Get(KeyReturnUrl), "pay-result"),
                Pid = Get(KeySandboxPid)
            };
        }

        // ③ 模拟：无需任何凭证
        return new PayRuntime { Mode = ModeMock };
    }

    /// <summary>解析某个密钥项的来源（env / secrets / db / none），只回来源不回值</summary>
    private async Task<Dictionary<string, string>> ResolveSecretSourcesAsync(IEnumerable<string> keys)
    {
        var raw = await GetRawAsync();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var dbValue = raw.GetValueOrDefault(key, "");
            _secrets.Resolve(key, dbValue, out var source);
            result[key] = source;
        }
        return result;
    }

    /// <summary>三条支付通道的状态汇总</summary>
    public async Task<PaymentChannelsDto> GetChannelsAsync()
    {
        var raw = await GetRawAsync();
        string Get(string key) => raw.GetValueOrDefault(key, "").Trim();

        var rt = await ResolveAsync();

        var alipay = new PaymentChannelDto
        {
            Key = "alipay",
            Name = "支付宝",
            Integrated = true,
            Enabled = rt.IsReal,
            Configured = rt.CredentialsComplete,
            Status = rt.Mode,
            Detail = rt.Mode switch
            {
                ModeProd => "生产凭证齐全，真实收款已生效。",
                ModeSandbox => "走支付宝沙箱，不产生真实资金。",
                _ => "凭证不完整，已内退到模拟收银台。"
            }
        };

        // 微信支付：WechatPayService 已实现 APIv3（H5 下单 + 查单 + 回调解密验签）。
        // 必填五项：AppId + 商户号 + APIv3 密钥 + 证书/公钥序号 + 商户私钥。
        string[] wxRequired = [KeyWechatAppId, KeyWechatMchId, KeyWechatApiV3Key, KeyWechatCertSerial, KeyWechatPrivateKey];
        var wxFilled = wxRequired.Count(k => Get(k).Length > 0);
        var wxOn = Get(KeyWechatEnabled) is "1" or "true" or "True";
        var wxConfigured = wxFilled == wxRequired.Length;

        var wechat = new PaymentChannelDto
        {
            Key = "wechat",
            Name = "微信支付",
            Integrated = true,
            Enabled = wxOn,
            Configured = wxConfigured,
            Status = wxOn && wxConfigured ? "已启用" : wxConfigured ? "已配置未启用" : "未配置",
            Detail = wxOn && wxConfigured
                ? "正式通道已生效：微信支付 APIv3（H5 收银台），回调验签 + 查单兜底入账，支持原路退款。"
                : wxConfigured
                    ? "凭证齐全但开关未打开，打开后即走微信支付 APIv3 正式通道。"
                    : $"凭证 {wxFilled}/{wxRequired.Length} 项已填；不齐全期间自动退回内置模拟收银台。"
        };

        // 银行卡：BankPayService 已实现通用收银网关协议（签名跳转 + 回调验签）。
        // 通用网关没有统一查单协议，入账依赖异步回调——这点必须在详情里讲清楚。
        string[] bankRequired = [KeyBankMerchantNo, KeyBankGateway, KeyBankSignKey];
        var bankFilled = bankRequired.Count(k => Get(k).Length > 0);
        var bankOn = Get(KeyBankEnabled) is "1" or "true" or "True";
        var bankConfigured = bankFilled == bankRequired.Length;

        var bank = new PaymentChannelDto
        {
            Key = "bank",
            Name = "银行卡",
            Integrated = true,
            Enabled = bankOn,
            Configured = bankConfigured,
            Status = bankOn && bankConfigured ? "已启用" : bankConfigured ? "已配置未启用" : "未配置",
            Detail = bankOn && bankConfigured
                ? "正式通道已生效：收银网关签名跳转，入账依赖异步回调（回调地址须公网可达），支持原路退款。"
                : bankConfigured
                    ? "凭证齐全但开关未打开，打开后即走银行卡收银网关正式通道。"
                    : $"凭证 {bankFilled}/{bankRequired.Length} 项已填；不齐全期间自动退回内置模拟收银台。"
        };

        return new PaymentChannelsDto { Items = [alipay, wechat, bank] };
    }

    /// <summary>把一组支付凭证写入 system_config（键不存在则新建）</summary>
    private async Task SaveAsync(IEnumerable<(string Key, string Value, string Desc)> items)
    {
        var list = items.ToList();
        var keys = list.Select(i => i.Key).ToList();
        var existing = await _db.SystemConfigs.Where(c => keys.Contains(c.ConfigKey)).ToListAsync();

        foreach (var (key, value, desc) in list)
        {
            var row = existing.FirstOrDefault(c => c.ConfigKey == key);
            if (row == null)
            {
                _db.SystemConfigs.Add(SystemConfig.Create(key, value, desc));
            }
            else
            {
                row.SetValue(value);
            }
        }
        await _db.SaveChangesAsync();
    }

    /// <summary>可查看明文的密钥字段（前端字段名 → system_config 键）</summary>
    public static readonly IReadOnlyDictionary<string, string> SecretFieldKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prodPrivateKey"] = KeyProdPrivateKey,
            ["prodAlipayPublicKey"] = KeyProdAlipayPublicKey,
            ["sandboxPrivateKey"] = KeySandboxPrivateKey,
            ["sandboxAlipayPublicKey"] = KeySandboxAlipayPublicKey,
            ["wechatApiV3Key"] = KeyWechatApiV3Key,
            ["wechatPrivateKey"] = KeyWechatPrivateKey,
            ["wechatPlatformKey"] = KeyWechatPlatformKey,
            ["wechatCertSerial"] = KeyWechatCertSerial,
            ["bankSignKey"] = KeyBankSignKey,
        };

    /// <summary>读取单个密钥项的明文，只给管理员在「支付配置」弹窗里点「显示」时按需取用</summary>
    public async Task<string?> GetSecretAsync(string? field)
    {
        var name = (field ?? "").Trim();
        if (!SecretFieldKeys.TryGetValue(name, out var key)) return null;

        var raw = await GetRawAsync();
        return raw.TryGetValue(key, out var v) ? (v ?? "").Trim() : "";
    }

    /// <summary>把已存的值脱敏后返回（私钥/公钥只留首尾几位），供管理员端表单回显</summary>
    private static string Mask(string? value, int head = 6, int tail = 6)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return "";
        if (v.Length <= head + tail) return new string('*', v.Length);
        return v[..head] + new string('*', Math.Min(20, v.Length - head - tail)) + v[^tail..];
    }

    /// <summary>组装管理员端「支付配置」回显视图（密钥脱敏 + 当前模式 + 模式原因）</summary>
    public async Task<PayConfigViewDto> GetViewAsync()
    {
        var raw = await GetRawAsync();
        string Get(string key) => raw.GetValueOrDefault(key, "").Trim();

        var prodPriv = Get(KeyProdPrivateKey);
        var prodPub = Get(KeyProdAlipayPublicKey);
        var sbPriv = Get(KeySandboxPrivateKey);
        var sbPub = Get(KeySandboxAlipayPublicKey);
        // 微信/银行卡的密钥类字段同样脱敏（管理员可能已提前填入真实值）
        var wxV3Key = Get(KeyWechatApiV3Key);
        var wxPriv = Get(KeyWechatPrivateKey);
        var bankKey = Get(KeyBankSignKey);

        // 密钥来源：只回来源名（env/secrets/db/none），**绝不回值**。
        // 管理员据此判断"我在页面填的到底生效了没"（被环境变量接管时页面改动不生效）。
        var secretKeys = new[]
        {
            KeyProdPrivateKey, KeyProdAlipayPublicKey,
            KeySandboxPrivateKey, KeySandboxAlipayPublicKey,
            KeyWechatApiV3Key, KeyWechatPrivateKey, KeyWechatPlatformKey, KeyBankSignKey
        };
        var sources = await ResolveSecretSourcesAsync(secretKeys);

        var dto = new PayConfigViewDto
        {
            ProdEnabled = Get(KeyProdEnabled) is "1" or "true" or "True",
            ProdAppId = Get(KeyProdAppId),
            ProdPid = Get(KeyProdPid),
            ProdPrivateKeyMasked = Mask(prodPriv),
            ProdAlipayPublicKeyMasked = Mask(prodPub),
            ProdGateway = Get(KeyProdGateway),
            ProdPrivateKeySet = prodPriv.Length > 0,
            ProdAlipayPublicKeySet = prodPub.Length > 0,

            SandboxAppId = Get(KeySandboxAppId),
            SandboxPid = Get(KeySandboxPid),
            SandboxPrivateKeyMasked = Mask(sbPriv),
            SandboxAlipayPublicKeyMasked = Mask(sbPub),
            SandboxGateway = Get(KeySandboxGateway),
            SandboxPrivateKeySet = sbPriv.Length > 0,
            SandboxAlipayPublicKeySet = sbPub.Length > 0,

            // 微信支付（正式通道）
            WechatEnabled = Get(KeyWechatEnabled) is "1" or "true" or "True",
            WechatAppId = Get(KeyWechatAppId),
            WechatMchId = Get(KeyWechatMchId),
            WechatApiV3KeyMasked = Mask(wxV3Key),
            WechatCertSerialMasked = Mask(Get(KeyWechatCertSerial)),
            WechatPrivateKeyMasked = Mask(wxPriv),
            WechatNotifyUrl = FallbackLocalUrl(Get(KeyWechatNotifyUrl), "api/payment/wechat/notify"),
            WechatGateway = Get(KeyWechatGateway),
            WechatPlatformKeyMasked = Mask(Get(KeyWechatPlatformKey)),
            WechatPayMode = NormalizeWechatPayMode(Get(KeyWechatPayMode)),
            WechatApiV3KeySet = wxV3Key.Length > 0,
            WechatPrivateKeySet = wxPriv.Length > 0,

            // 银行卡支付（正式通道）
            BankEnabled = Get(KeyBankEnabled) is "1" or "true" or "True",
            BankMerchantNo = Get(KeyBankMerchantNo),
            BankGateway = Get(KeyBankGateway),
            BankSignKeyMasked = Mask(bankKey),
            BankNotifyUrl = FallbackLocalUrl(Get(KeyBankNotifyUrl), "api/payment/bank/notify"),
            BankRefundUrl = Get(KeyBankRefundUrl),
            BankQueryUrl = Get(KeyBankQueryUrl),
            BankBalanceUrl = Get(KeyBankBalanceUrl),
            BankSignKeySet = bankKey.Length > 0,

            SecretSources = sources,

            NotifyUrl = FallbackLocalUrl(Get(KeyNotifyUrl), "api/payment/alipay/notify"),
            ReturnUrl = FallbackLocalUrl(Get(KeyReturnUrl), "pay-result"),
            // 库里为空 = 当前显示的不是"已配置"，而是运行时推算出来的兜底值
            NotifyUrlAuto = Get(KeyNotifyUrl).Length == 0,
            ReturnUrlAuto = Get(KeyReturnUrl).Length == 0
        };

        var rt = await ResolveAsync();
        dto.Mode = rt.Mode;
        dto.ModeReason = rt.Mode switch
        {
            ModeProd => "已启用生产支付，且生产 AppId / 应用私钥 / 支付宝公钥 三件套齐全 → 走支付宝生产网关。",
            ModeSandbox => dto.ProdEnabled
                ? "生产支付开关已打开，但生产凭证不全 → 自动降级到沙箱。"
                : "沙箱 AppId / 应用私钥 / 支付宝公钥 三件套齐全 → 走支付宝沙箱网关。",
            _ => "生产与沙箱凭证均不完整 → 自动降级到模拟收银台（不联网、不验签，链路仍完整）。"
        };
        return dto;
    }

    /// <summary>保存管理员填写的支付凭证</summary>
    public async Task SaveViewAsync(SavePayConfigDto dto)
    {
        var raw = await GetRawAsync();
        string KeepIfEmpty(string? incoming, string key)
        {
            // 入参为 null 表示「不修改」，必须与空串同等对待：这里不能用 .Trim() 直取
            var v = (incoming ?? "").Trim();
            if (v.Length == 0) return raw.GetValueOrDefault(key, "");
            return v == "CLEAR" ? "" : v;
        }

        // 开关同理：null = 本次不涉及该渠道的启停（例如工作台的「账户绑定」弹窗只改账号标识），
        // 必须保持库里的原值。早期实现写的是 `is true ? "1" : "0"`，null 会被当成 false，
        // 结果只保存一个支付宝 PID 就把微信、银行卡、生产环境三个开关一起关掉。
        string KeepEnabled(bool? incoming, string key)
            => incoming is bool b ? (b ? "1" : "0") : raw.GetValueOrDefault(key, "0");

        var items = new List<(string Key, string Value, string Desc)>
        {
            (KeyProdEnabled, KeepEnabled(dto.ProdEnabled, KeyProdEnabled), "支付-启用生产环境（1开0关）"),
            (KeyProdAppId, KeepIfEmpty(dto.ProdAppId, KeyProdAppId), "支付-生产 AppId"),
            (KeyProdPid, KeepIfEmpty(dto.ProdPid, KeyProdPid), "支付-生产支付宝会员 ID（PID，查余额用）"),
            (KeyProdPrivateKey, KeepIfEmpty(dto.ProdPrivateKey, KeyProdPrivateKey), "支付-生产应用私钥"),
            (KeyProdAlipayPublicKey, KeepIfEmpty(dto.ProdAlipayPublicKey, KeyProdAlipayPublicKey), "支付-生产支付宝公钥"),
            (KeyProdGateway, KeepIfEmpty(dto.ProdGateway, KeyProdGateway), "支付-生产网关"),

            (KeySandboxAppId, KeepIfEmpty(dto.SandboxAppId, KeySandboxAppId), "支付-沙箱 AppId"),
            (KeySandboxPid, KeepIfEmpty(dto.SandboxPid, KeySandboxPid), "支付-沙箱支付宝会员 ID（PID，查余额用）"),
            (KeySandboxPrivateKey, KeepIfEmpty(dto.SandboxPrivateKey, KeySandboxPrivateKey), "支付-沙箱应用私钥"),
            (KeySandboxAlipayPublicKey, KeepIfEmpty(dto.SandboxAlipayPublicKey, KeySandboxAlipayPublicKey), "支付-沙箱支付宝公钥"),
            (KeySandboxGateway, KeepIfEmpty(dto.SandboxGateway, KeySandboxGateway), "支付-沙箱网关"),

            // 微信支付（正式通道：WechatPayService 走 APIv3——下单 + 查单 + 回调解密验签；
            // 下单方式按 wechat_pay_mode 分派：h5 跳转收银台 / native 扫码；
            // 凭证不齐全期间自动退回内置模拟收银台，与支付宝的降级思路一致）
            (KeyWechatEnabled, KeepEnabled(dto.WechatEnabled, KeyWechatEnabled), "支付-启用微信支付"),
            (KeyWechatAppId, KeepIfEmpty(dto.WechatAppId, KeyWechatAppId), "支付-微信 AppId"),
            (KeyWechatMchId, KeepIfEmpty(dto.WechatMchId, KeyWechatMchId), "支付-微信商户号 MchId"),
            (KeyWechatApiV3Key, KeepIfEmpty(dto.WechatApiV3Key, KeyWechatApiV3Key), "支付-微信 APIv3 密钥（回调解密用）"),
            (KeyWechatCertSerial, KeepIfEmpty(dto.WechatCertSerial, KeyWechatCertSerial), "支付-微信商户 API 证书序列号（请求签名用）"),
            (KeyWechatPrivateKey, KeepIfEmpty(dto.WechatPrivateKey, KeyWechatPrivateKey), "支付-微信商户私钥（请求签名用）"),
            (KeyWechatNotifyUrl, KeepIfEmpty(dto.WechatNotifyUrl, KeyWechatNotifyUrl), "支付-微信异步回调地址"),
            (KeyWechatGateway, KeepIfEmpty(dto.WechatGateway, KeyWechatGateway), "支付-微信 APIv3 网关（留空用官方默认）"),
            (KeyWechatPlatformKey, KeepIfEmpty(dto.WechatPlatformKey, KeyWechatPlatformKey), "支付-微信支付公钥（回调验签用）"),
            // 支付方式不走 KeepIfEmpty 的「留空保持」语义：下拉框永远有值，空值只可能来自旧版前端，
            // 此时保持库里原值，避免一次无关保存把已经选好的 native 打回 h5。
            (KeyWechatPayMode, string.IsNullOrWhiteSpace(dto.WechatPayMode)
                ? raw.GetValueOrDefault(KeyWechatPayMode, WechatPayModeH5)
                : NormalizeWechatPayMode(dto.WechatPayMode), "支付-微信支付方式（h5 跳转 / native 扫码）"),

            // 银行卡支付（正式通道：BankPayService 走通用收银网关——签名跳转 + 回调验签。
            // 网关无统一协议，查单/余额/退款三条路径均可推导，也可由管理员显式指定）
            (KeyBankEnabled, KeepEnabled(dto.BankEnabled, KeyBankEnabled), "支付-启用银行卡支付"),
            (KeyBankMerchantNo, KeepIfEmpty(dto.BankMerchantNo, KeyBankMerchantNo), "支付-银行卡商户号"),
            (KeyBankGateway, KeepIfEmpty(dto.BankGateway, KeyBankGateway), "支付-银行卡网关"),
            (KeyBankSignKey, KeepIfEmpty(dto.BankSignKey, KeyBankSignKey), "支付-银行卡签名密钥"),
            (KeyBankNotifyUrl, KeepIfEmpty(dto.BankNotifyUrl, KeyBankNotifyUrl), "支付-银行卡异步回调地址"),
            (KeyBankRefundUrl, KeepIfEmpty(dto.BankRefundUrl, KeyBankRefundUrl), "支付-银行卡退款地址（留空按网关推导）"),
            (KeyBankQueryUrl, KeepIfEmpty(dto.BankQueryUrl, KeyBankQueryUrl), "支付-银行卡查单地址（留空按网关推导）"),
            (KeyBankBalanceUrl, KeepIfEmpty(dto.BankBalanceUrl, KeyBankBalanceUrl), "支付-银行卡余额地址（留空按网关推导）"),

            (KeyNotifyUrl, KeepIfEmpty(dto.NotifyUrl, KeyNotifyUrl), "支付-异步回调地址（须公网可达）"),
            (KeyReturnUrl, KeepIfEmpty(dto.ReturnUrl, KeyReturnUrl), "支付-同步跳转地址")
        };

        // 网关留空时补默认值，避免管理员不填网关导致配置"看着齐全却跑不通"
        foreach (var (key, value, _) in items.ToList())
        {
            if (key == KeyProdGateway && value.Length == 0)
                items[items.FindIndex(i => i.Key == key)] = (key, DefaultProdGateway, "支付-生产网关");
            if (key == KeySandboxGateway && value.Length == 0)
                items[items.FindIndex(i => i.Key == key)] = (key, DefaultSandboxGateway, "支付-沙箱网关");
        }

        await SaveAsync(items);
    }
}

/// <summary>一次支付调用所需的运行参数，由 产出</summary>
public class PayRuntime
{
    public string Mode { get; init; } = PayConfigService.ModeMock;

    public string AppId { get; init; } = string.Empty;
    public string PrivateKey { get; init; } = string.Empty;
    public string AlipayPublicKey { get; init; } = string.Empty;
    public string Gateway { get; init; } = string.Empty;
    public string NotifyUrl { get; init; } = string.Empty;
    public string ReturnUrl { get; init; } = string.Empty;

    /// <summary>商户 / 应用对应的支付宝会员 ID（PID，16 位数字）</summary>
    public string Pid { get; init; } = string.Empty;

    /// <summary>是否走真实支付宝（生产或沙箱）</summary>
    public bool IsReal => Mode != PayConfigService.ModeMock;

    /// <summary>真实模式下凭证是否齐全（模拟模式恒 false，用于区分"为什么不是真实支付"）</summary>
    public bool CredentialsComplete =>
        !string.IsNullOrWhiteSpace(AppId)
        && !string.IsNullOrWhiteSpace(PrivateKey)
        && !string.IsNullOrWhiteSpace(AlipayPublicKey);
}

using System.Text.RegularExpressions;
using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using ERental.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERental.Services;

///<summary>支付凭证的存取与「三级自动降级」判定</summary>
public partial class PayConfigService(AppDbContext db, IOptions<LanOptions> lan, SecretProvider secrets)
{
    private readonly AppDbContext _db = db;
    private readonly SecretProvider _secrets = secrets;

    ///<summary>局域网回环地址识别：整段 localhost/127</summary>
    [GeneratedRegex(@"^https?://(localhost|127\.0\.0\.1)(:|/|$)")]
    private static partial Regex LocalHostUrlRegex();

    ///<summary>局域网回环前缀（含可选端口）：用于把历史遗留地址整段重写为本机 IP+当前端口</summary>
    [GeneratedRegex(@"^https?://(localhost|127\.0\.0\.1)(:\d+)?")]
    private static partial Regex LocalHostPrefixRegex();

    ///<summary>局域网模式下按需覆盖为「本机 IP+当前前端端口」</summary>
    private readonly LanOptions _lan = lan.Value;

    ///<summary>支付配置项的键前缀</summary>
    public const string Prefix = "pay.";

    //── 生产环境凭证 ──
    public const string KeyProdEnabled = Prefix + "prod_enabled";
    public const string KeyProdAppId = Prefix + "prod_appid";
    public const string KeyProdPrivateKey = Prefix + "prod_private_key";
    public const string KeyProdGateway = Prefix + "prod_gateway";

    //── 沙箱环境凭证 ──
    public const string KeySandboxAppId = Prefix + "sandbox_appid";
    public const string KeySandboxPrivateKey = Prefix + "sandbox_private_key";
    public const string KeySandboxGateway = Prefix + "sandbox_gateway";

    //── 两级共用的支付宝公钥（验签用） ──支付宝公钥是"支付宝的"公钥
    public const string KeyProdAlipayPublicKey = Prefix + "prod_alipay_public_key";
    public const string KeySandboxAlipayPublicKey = Prefix + "sandbox_alipay_public_key";

    //── 支付宝会员 ID
    private const string KeyProdPid = Prefix + "prod_pid";
    private const string KeySandboxPid = Prefix + "sandbox_pid";

    //── 回调地址
    public const string KeyNotifyUrl = Prefix + "notify_url";
    public const string KeyReturnUrl = Prefix + "return_url";

    //══════════════════════════════════════════════════════════════════「微信支付/银行卡支付」的配置键

    //── 微信支付（APIv3） ──cert_serial：商户 API 证书序列号（40 位十六进制）
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

    ///<summary>微信支付方式：H5 跳转（历史默认）</summary>
    public const string WechatPayModeH5 = "h5";

    ///<summary>微信支付方式：Native 扫码</summary>
    public const string WechatPayModeNative = "native";

    //── 银行卡支付（通用收银网关） ──网关协议采用业内通行的「参数签名跳转+回调验签」：选定机构后填其网关地址与签名密钥即可
    public const string KeyBankEnabled = Prefix + "bank_enabled";
    public const string KeyBankMerchantNo = Prefix + "bank_merchant_no";
    public const string KeyBankGateway = Prefix + "bank_gateway";
    public const string KeyBankSignKey = Prefix + "bank_sign_key";
    public const string KeyBankNotifyUrl = Prefix + "bank_notify_url";
    //退款地址非必填：留空时由支付网关推导（/pay → /refund，其余追加 /refund）
    public const string KeyBankRefundUrl = Prefix + "bank_refund_url";

    //查单/余额地址同样非必填：通用收银网关没有统一协议
    public const string KeyBankQueryUrl = Prefix + "bank_query_url";
    public const string KeyBankBalanceUrl = Prefix + "bank_balance_url";

    ///<summary>支付模式：生产/沙箱/模拟</summary>
    public const string ModeProd = "生产";
    private const string ModeSandbox = "沙箱";
    public const string ModeMock = "模拟";

    ///<summary>把支付方式归一到合法取值</summary>
    public static string NormalizeWechatPayMode(string? value) => string.Equals(value?.Trim(), WechatPayModeNative, StringComparison.OrdinalIgnoreCase) ? WechatPayModeNative : WechatPayModeH5;

    private const string DefaultProdGateway = "https://openapi.alipay.com/gateway.do";
    private const string DefaultSandboxGateway = "https://openapi-sandbox.dl.alipaydev.com/gateway.do";

    ///<summary>回调地址兜底：管理员没填（或填的仍是 localhost）时，按当前运行环境给一个能用的值</summary>
    private string FallbackLocalUrl(string configured, string path)
    {
        var value = configured.Trim();
        string? ip = null;
        if (_lan.Enabled) ip = string.IsNullOrWhiteSpace(_lan.Host) ? NetworkHelper.ResolveLanIp() : _lan.Host.Trim();

        //非局域网模式：原样返回，行为与改造前完全一致
        if (string.IsNullOrWhiteSpace(ip)) return value;

        //后端自身监听的明文端口
        var notifyBase = $"http://{ip}:5105";//NOSONAR 后端只监听明文端口，TLS 在外层反向代理终止
        var isNotify = path.Contains("notify");
        //前端入口：协议由 Lan:FrontScheme 决定，默认 https
        var frontBase = $"{NormalizeFrontScheme(_lan.FrontScheme)}://{ip}:{_lan.CustomerPort}";

        if (string.IsNullOrWhiteSpace(value)) return isNotify ? $"{notifyBase}/{path}" : $"{frontBase}/{path}";

        //局域网模式下，历史遗留的 localhost 地址必须整段重写：只替换主机名的话
        if (LocalHostUrlRegex().IsMatch(value)) return LocalHostPrefixRegex().Replace(value, isNotify ? notifyBase : frontBase);

        return value.Replace("localhost", ip).Replace("127.0.0.1", ip);
    }

    ///<summary>只接受 http/https，其余（含空值、历史脏值）按 https 处理</summary>
    private static string NormalizeFrontScheme(string? scheme) => string.Equals(scheme?.Trim(), "http", StringComparison.OrdinalIgnoreCase) ? "http" : "https";

    ///<summary>回跳地址兜底的公开入口（微信/银行卡通道构建跳转与回调地址用）</summary>
    public string FallbackChannelUrl(string configured, string path) => FallbackLocalUrl(configured, path);

    ///<summary>从请求头还原「用户此刻访问的源」，供调用方拼回跳/通知地址</summary>
    public static string? ResolveClientOrigin(string? explicitOrigin, string? referer)
    {
        if (!string.IsNullOrWhiteSpace(explicitOrigin)) return explicitOrigin.Trim();
        if (string.IsNullOrWhiteSpace(referer)) return null;
        return Uri.TryCreate(referer.Trim(), UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Authority}" : null;
    }

    ///<summary>构建「支付完成后回跳的前端地址」</summary>
    public async Task<string> BuildReturnUrlAsync(string? origin, string path)
    {
        var normalized = NormalizeOrigin(origin);
        if (normalized != null) return $"{normalized}/{path.TrimStart('/')}";

        var raw = await GetRawAsync();
        var configured = raw.TryGetValue(KeyReturnUrl, out var v) ? v.Trim() : "";
        return FallbackChannelUrl(configured, path);
    }

    ///<summary>构建「支付平台异步通知地址」</summary>
    public async Task<string> BuildNotifyUrlAsync(string? origin, string path)
    {
        var normalized = NormalizeOrigin(origin);
        if (normalized != null) return $"{normalized}/{path.TrimStart('/')}";

        var raw = await GetRawAsync();
        var configured = raw.TryGetValue(KeyNotifyUrl, out var v) ? v.Trim() : "";
        return FallbackChannelUrl(configured, path);
    }

    ///<summary>校验并规范化浏览器传来的 origin</summary>
    private static string? NormalizeOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return null;
        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    ///<summary>敏感项分层解析的公开入口（env＞secrets＞db），供各支付服务复用同一套优先级</summary>
    public string Secret(string key, string dbValue) => _secrets.Resolve(key, dbValue, out _);

    ///<summary>读出全部支付配置</summary>
    public async Task<Dictionary<string, string>> GetRawAsync()
    {
        var rows = await _db.SystemConfigs.AsNoTracking().Where(c => c.ConfigKey.StartsWith(Prefix)).ToListAsync();

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            dict[r.ConfigKey] = r.ConfigValue ?? string.Empty;
        return dict;
    }

    ///<summary>判定当前生效的支付模式+组装出该模式的接入参数PayRuntime 已收窄为 internal</summary>
    internal async Task<PayRuntime> ResolveAsync()
    {
        var raw = await GetRawAsync();

        string Get(string key) => raw.GetValueOrDefault(key, "").Trim();
        //敏感项：按分层优先级取值（env>secrets>db）
        string SecretOf(string key) => _secrets.Resolve(key, Get(key), out _);
        bool ProdSwitchOn() => Get(KeyProdEnabled) is "1" or "true" or "True";

        //① 生产：开关打开+三件套齐全
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

        //② 沙箱：三件套齐全
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

        //③ 模拟：无需任何凭证
        return new PayRuntime { Mode = ModeMock };
    }

    ///<summary>解析某个密钥项的来源（env/secrets/db/none），只回来源不回值</summary>
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

    ///<summary>三条支付通道的状态汇总</summary>
    public async Task<PaymentChannelsDto> GetChannelsAsync()
    {
        var raw = await GetRawAsync();
        string Get(string key) => raw.GetValueOrDefault(key, "").Trim();

        var rt = await ResolveAsync();
        var alipay = BuildAlipayChannel(rt);
        var wechat = BuildWechatChannel(Get);
        var bank = BuildBankChannel(Get);

        return new PaymentChannelsDto { Items = [alipay, wechat, bank] };
    }

    ///<summary>支付宝通道：模式直接取自运行时判定结果</summary>
    private static PaymentChannelDto BuildAlipayChannel(PayRuntime rt) => new()
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

    ///<summary>微信通道：五项必填（AppId+商户号+APIv3 密钥+证书序列号+商户私钥）</summary>
    private static PaymentChannelDto BuildWechatChannel(Func<string, string> get)
    {
        //微信支付：WechatPayService 已实现 APIv3（H5 下单+查单+回调解密验签）
        string[] required = [KeyWechatAppId, KeyWechatMchId, KeyWechatApiV3Key, KeyWechatCertSerial, KeyWechatPrivateKey];
        var filled = required.Count(k => get(k).Length > 0);
        var on = get(KeyWechatEnabled) is "1" or "true" or "True";
        var configured = filled == required.Length;

        return new PaymentChannelDto
        {
            Key = "wechat",
            Name = "微信支付",
            Integrated = true,
            Enabled = on,
            Configured = configured,
            Status = ChannelStatus(on, configured),
            Detail = ChannelDetail(on, configured,
                "正式通道已生效：微信支付 APIv3（H5 收银台），回调验签 + 查单兜底入账，支持原路退款。",
                "凭证齐全但开关未打开，打开后即走微信支付 APIv3 正式通道。",
                $"凭证 {filled}/{required.Length} 项已填；不齐全期间自动退回内置模拟收银台。")
        };
    }

    ///<summary>银行卡通道：三项必填（商户号+收银网关+签名密钥）</summary>
    private static PaymentChannelDto BuildBankChannel(Func<string, string> get)
    {
        //银行卡：BankPayService 已实现通用收银网关协议（签名跳转+回调验签）
        string[] required = [KeyBankMerchantNo, KeyBankGateway, KeyBankSignKey];
        var filled = required.Count(k => get(k).Length > 0);
        var on = get(KeyBankEnabled) is "1" or "true" or "True";
        var configured = filled == required.Length;

        return new PaymentChannelDto
        {
            Key = "bank",
            Name = "银行卡",
            Integrated = true,
            Enabled = on,
            Configured = configured,
            Status = ChannelStatus(on, configured),
            Detail = ChannelDetail(on, configured,
                "正式通道已生效：收银网关签名跳转，入账依赖异步回调（回调地址须公网可达），支持原路退款。",
                "凭证齐全但开关未打开，打开后即走银行卡收银网关正式通道。",
                $"凭证 {filled}/{required.Length} 项已填；不齐全期间自动退回内置模拟收银台。")
        };
    }

    ///<summary>渠道状态词：开关+凭证两项判定</summary>
    private static string ChannelStatus(bool on, bool configured)
    {
        if (on && configured) return "已启用";
        return configured ? "已配置未启用" : "未配置";
    }

    ///<summary>渠道详情文案：已启用/已配置未启用/凭证不全 三档</summary>
    private static string ChannelDetail(bool on, bool configured, string activeText, string readyText, string incompleteText)
    {
        if (on && configured) return activeText;
        return configured ? readyText : incompleteText;
    }

    ///<summary>把一组支付凭证写入 system_config（键不存在则新建）</summary>
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

    ///<summary>可查看明文的密钥字段</summary>
    public static readonly IReadOnlyDictionary<string, string> SecretFieldKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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

    ///<summary>读取单个密钥项的明文，只给管理员在「支付配置」弹窗里点「显示」时按需取用</summary>
    public async Task<string?> GetSecretAsync(string? field)
    {
        var name = (field ?? "").Trim();
        if (!SecretFieldKeys.TryGetValue(name, out var key)) return null;

        var raw = await GetRawAsync();
        return raw.TryGetValue(key, out var v) ? (v ?? "").Trim() : "";
    }

    ///<summary>把已存的值脱敏后返回（私钥/公钥只留首尾几位），供管理员端表单回显</summary>
    private static string Mask(string? value, int head = 6, int tail = 6)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return "";
        if (v.Length <= head + tail) return new string('*', v.Length);
        return v[..head] + new string('*', Math.Min(20, v.Length - head - tail)) + v[^tail..];
    }

    ///<summary>组装管理员端「支付配置」回显视图（密钥脱敏+当前模式+模式原因）</summary>
    public async Task<PayConfigViewDto> GetViewAsync()
    {
        var raw = await GetRawAsync();
        string Get(string key) => raw.GetValueOrDefault(key, "").Trim();

        var prodPriv = Get(KeyProdPrivateKey);
        var prodPub = Get(KeyProdAlipayPublicKey);
        var sbPriv = Get(KeySandboxPrivateKey);
        var sbPub = Get(KeySandboxAlipayPublicKey);
        //微信/银行卡的密钥类字段同样脱敏（管理员可能已提前填入真实值）
        var wxV3Key = Get(KeyWechatApiV3Key);
        var wxPriv = Get(KeyWechatPrivateKey);
        var bankKey = Get(KeyBankSignKey);

        //密钥来源：只回来源名（env/secrets/db/none），绝不回值
        var secretKeys = new[]
        {
            KeyProdPrivateKey,KeyProdAlipayPublicKey,
            KeySandboxPrivateKey,KeySandboxAlipayPublicKey,
            KeyWechatApiV3Key,KeyWechatPrivateKey,KeyWechatPlatformKey,KeyBankSignKey
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

            //微信支付（正式通道）
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

            //银行卡支付（正式通道）
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
            //库里为空=当前显示的不是"已配置"，而是运行时推算出来的兜底值
            NotifyUrlAuto = Get(KeyNotifyUrl).Length == 0,
            ReturnUrlAuto = Get(KeyReturnUrl).Length == 0
        };

        var rt = await ResolveAsync();
        dto.Mode = rt.Mode;
        dto.ModeReason = rt.Mode switch
        {
            ModeProd => "已启用生产支付，且生产 AppId / 应用私钥 / 支付宝公钥 三件套齐全 → 走支付宝生产网关。",
            ModeSandbox => dto.ProdEnabled ? "生产支付开关已打开，但生产凭证不全 → 自动降级到沙箱。" : "沙箱 AppId / 应用私钥 / 支付宝公钥 三件套齐全 → 走支付宝沙箱网关。",
            _ => "生产与沙箱凭证均不完整 → 自动降级到模拟收银台（不联网、不验签，链路仍完整）。"
        };
        return dto;
    }

    ///<summary>保存管理员填写的支付凭证</summary>
    public async Task SaveViewAsync(SavePayConfigDto dto)
    {
        var raw = await GetRawAsync();
        string KeepIfEmpty(string? incoming, string key)
        {
            //入参为 null 表示「不修改」，必须与空串同等对待：这里不能用 .Trim 直取
            var v = (incoming ?? "").Trim();
            if (v.Length == 0) return raw.GetValueOrDefault(key, "");
            return v == "CLEAR" ? "" : v;
        }

        //开关同理：null=本次不涉及该渠道的启停（例如工作台的「账户绑定」弹窗只改账号标识）
        string KeepEnabled(bool? incoming, string key)
        {
            if (incoming is not bool b) return raw.GetValueOrDefault(key, "0");
            return b ? "1" : "0";
        }

        var items = new List<(string Key, string Value, string Desc)>
        {
            (KeyProdEnabled,KeepEnabled(dto.ProdEnabled,KeyProdEnabled),"支付-启用生产环境（1开0关）"),
            (KeyProdAppId,KeepIfEmpty(dto.ProdAppId,KeyProdAppId),"支付-生产 AppId"),
            (KeyProdPid,KeepIfEmpty(dto.ProdPid,KeyProdPid),"支付-生产支付宝会员 ID（PID，查余额用）"),
            (KeyProdPrivateKey,KeepIfEmpty(dto.ProdPrivateKey,KeyProdPrivateKey),"支付-生产应用私钥"),
            (KeyProdAlipayPublicKey,KeepIfEmpty(dto.ProdAlipayPublicKey,KeyProdAlipayPublicKey),"支付-生产支付宝公钥"),
            (KeyProdGateway,KeepIfEmpty(dto.ProdGateway,KeyProdGateway),"支付-生产网关"),

            (KeySandboxAppId,KeepIfEmpty(dto.SandboxAppId,KeySandboxAppId),"支付-沙箱 AppId"),
            (KeySandboxPid,KeepIfEmpty(dto.SandboxPid,KeySandboxPid),"支付-沙箱支付宝会员 ID（PID，查余额用）"),
            (KeySandboxPrivateKey,KeepIfEmpty(dto.SandboxPrivateKey,KeySandboxPrivateKey),"支付-沙箱应用私钥"),
            (KeySandboxAlipayPublicKey,KeepIfEmpty(dto.SandboxAlipayPublicKey,KeySandboxAlipayPublicKey),"支付-沙箱支付宝公钥"),
            (KeySandboxGateway,KeepIfEmpty(dto.SandboxGateway,KeySandboxGateway),"支付-沙箱网关"),

            //微信支付
            (KeyWechatEnabled,KeepEnabled(dto.WechatEnabled,KeyWechatEnabled),"支付-启用微信支付"),
            (KeyWechatAppId,KeepIfEmpty(dto.WechatAppId,KeyWechatAppId),"支付-微信 AppId"),
            (KeyWechatMchId,KeepIfEmpty(dto.WechatMchId,KeyWechatMchId),"支付-微信商户号 MchId"),
            (KeyWechatApiV3Key,KeepIfEmpty(dto.WechatApiV3Key,KeyWechatApiV3Key),"支付-微信 APIv3 密钥（回调解密用）"),
            (KeyWechatCertSerial,KeepIfEmpty(dto.WechatCertSerial,KeyWechatCertSerial),"支付-微信商户 API 证书序列号（请求签名用）"),
            (KeyWechatPrivateKey,KeepIfEmpty(dto.WechatPrivateKey,KeyWechatPrivateKey),"支付-微信商户私钥（请求签名用）"),
            (KeyWechatNotifyUrl,KeepIfEmpty(dto.WechatNotifyUrl,KeyWechatNotifyUrl),"支付-微信异步回调地址"),
            (KeyWechatGateway,KeepIfEmpty(dto.WechatGateway,KeyWechatGateway),"支付-微信 APIv3 网关（留空用官方默认）"),
            (KeyWechatPlatformKey,KeepIfEmpty(dto.WechatPlatformKey,KeyWechatPlatformKey),"支付-微信支付公钥（回调验签用）"),
            //支付方式不走 KeepIfEmpty 的「留空保持」语义
            (KeyWechatPayMode,string.IsNullOrWhiteSpace(dto.WechatPayMode)
                ? raw.GetValueOrDefault(KeyWechatPayMode,WechatPayModeH5)
                : NormalizeWechatPayMode(dto.WechatPayMode),"支付-微信支付方式（h5 跳转 / native 扫码）"),

            //银行卡支付
            (KeyBankEnabled,KeepEnabled(dto.BankEnabled,KeyBankEnabled),"支付-启用银行卡支付"),
            (KeyBankMerchantNo,KeepIfEmpty(dto.BankMerchantNo,KeyBankMerchantNo),"支付-银行卡商户号"),
            (KeyBankGateway,KeepIfEmpty(dto.BankGateway,KeyBankGateway),"支付-银行卡网关"),
            (KeyBankSignKey,KeepIfEmpty(dto.BankSignKey,KeyBankSignKey),"支付-银行卡签名密钥"),
            (KeyBankNotifyUrl,KeepIfEmpty(dto.BankNotifyUrl,KeyBankNotifyUrl),"支付-银行卡异步回调地址"),
            (KeyBankRefundUrl,KeepIfEmpty(dto.BankRefundUrl,KeyBankRefundUrl),"支付-银行卡退款地址（留空按网关推导）"),
            (KeyBankQueryUrl,KeepIfEmpty(dto.BankQueryUrl,KeyBankQueryUrl),"支付-银行卡查单地址（留空按网关推导）"),
            (KeyBankBalanceUrl,KeepIfEmpty(dto.BankBalanceUrl,KeyBankBalanceUrl),"支付-银行卡余额地址（留空按网关推导）"),

            (KeyNotifyUrl,KeepIfEmpty(dto.NotifyUrl,KeyNotifyUrl),"支付-异步回调地址（须公网可达）"),
            (KeyReturnUrl,KeepIfEmpty(dto.ReturnUrl,KeyReturnUrl),"支付-同步跳转地址")
        };

        //网关留空时补默认值
        foreach (var (key, value, _) in items.ToList())
        {
            if (key == KeyProdGateway && value.Length == 0) items[items.FindIndex(i => i.Key == key)] = (key, DefaultProdGateway, "支付-生产网关");
            if (key == KeySandboxGateway && value.Length == 0) items[items.FindIndex(i => i.Key == key)] = (key, DefaultSandboxGateway, "支付-沙箱网关");
        }

        await SaveAsync(items);
    }
}

///<summary>一次支付调用所需的运行参数，由 产出</summary>
internal sealed class PayRuntime
{
    public string Mode { get; init; } = PayConfigService.ModeMock;

    public string AppId { get; init; } = string.Empty;
    public string PrivateKey { get; init; } = string.Empty;
    public string AlipayPublicKey { get; init; } = string.Empty;
    public string Gateway { get; init; } = string.Empty;
    public string NotifyUrl { get; init; } = string.Empty;
    public string ReturnUrl { get; init; } = string.Empty;

    ///<summary>商户/应用对应的支付宝会员 ID（PID，16 位数字）</summary>
    public string Pid { get; init; } = string.Empty;

    ///<summary>是否走真实支付宝（生产或沙箱）</summary>
    public bool IsReal => Mode != PayConfigService.ModeMock;

    ///<summary>真实模式下凭证是否齐全（模拟模式恒 false，用于区分"为什么不是真实支付"）</summary>
    public bool CredentialsComplete => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(PrivateKey) && !string.IsNullOrWhiteSpace(AlipayPublicKey);
}

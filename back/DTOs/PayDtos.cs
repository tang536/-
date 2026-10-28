namespace ERental.DTOs;

/// <summary>发起支付请求：按订单号 + 支付方式创建支付</summary>
public class CreatePayDto
{
    /// <summary>订单号（ZL 开头）</summary>
    public string OrderNo { get; init; } = string.Empty;

    /// <summary>支付方式：支付宝 / 微信 / 银行卡</summary>
    public string PayType { get; init; } = "支付宝";
}

/// <summary>发起支付结果：把收银台地址回给前端跳转</summary>
public class CreatePayResultDto
{
    public string OrderNo { get; init; } = string.Empty;
    public string PayType { get; init; } = string.Empty;
    public decimal Amount { get; init; }

    /// <summary>收银台跳转地址（真实支付宝为官方网关；未配置时为内置模拟收银台）</summary>
    public string PayUrl { get; init; } = string.Empty;

    /// <summary>
    /// 需要前端渲染成二维码的内容（微信 Native 下单返回的 code_url）。
    /// 与 <see cref="PayUrl"/> 互斥：本字段非空时前端应展示二维码并轮询，而不是整页跳转。
    /// </summary>
    public string QrCode { get; init; } = string.Empty;

    /// <summary>实际生效的微信下单方式：h5 / native。非微信通道为空串</summary>
    public string PayMode { get; init; } = string.Empty;

    /// <summary>是否真实对接支付通道（false 表示走内置模拟收银台）</summary>
    public bool RealPay { get; init; }
}

/// <summary>支付状态查询结果</summary>
public class PayStatusDto
{
    public string OrderNo { get; init; } = string.Empty;

    /// <summary>
    /// 订单主键；会员购买（HY 交易号）无租赁订单，取 0。
    /// 前端支付成功时要立刻拉小票（print/order-receipt 按 orderId 取），
    /// 而回跳页 / 从支付页返回时前端可能还没加载过订单详情，
    /// 没有这个字段就只能靠「碰巧本地有 order 对象」侥幸成功。
    /// </summary>
    public int OrderId { get; init; }

    public string OrderStatus { get; init; } = string.Empty;
    public bool Paid { get; init; }
    public decimal Amount { get; init; }
    public string TransactionNo { get; init; } = string.Empty;
}

/// <summary>支付宝接线自检结果：一键回答「当前配置能不能用」</summary>
public class AlipaySelfCheckDto
{
    /// <summary>当前生效的支付模式：生产 / 沙箱 / 模拟</summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>是否已启用真实支付宝（生产或沙箱）</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// AppId / 私钥 / 支付宝公钥是否**真正可用**（不只是非空）。
    /// 私钥解析不出公钥时这里会是 false —— 那种状态下模式虽仍判定为生产/沙箱，
    /// 但任何签名请求都会失败，界面必须把"模式"和"能不能用"分开说。
    /// </summary>
    public bool Configured { get; set; }

    public string AppId { get; init; } = string.Empty;
    public string Gateway { get; init; } = string.Empty;

    /// <summary>沙箱是否接受了本次签名（true = 密钥配对正确）</summary>
    public bool SignatureAccepted { get; set; }

    /// <summary>由本地私钥推导出的「应用公钥」，应与沙箱后台登记的一致</summary>
    public string ExpectedAppPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// 上面那把应用公钥的模数指纹（SHA256 前 8 字节十六进制）。
    /// 作用：指纹比 392 字符的整串公钥好对比——若沙箱后台登记的密钥正确，
    /// 两边的指纹必然一致；不一致就说明登记的不是同一把密钥。
    /// 指数固定 65537，故模数相同即同一密钥。
    /// </summary>
    public string ExpectedAppPublicKeyFingerprint { get; set; } = string.Empty;

    /// <summary>本地私钥格式（PKCS#1 / PKCS#8），便于排查粘贴格式问题</summary>
    public string PrivateKeyFormat { get; set; } = string.Empty;

    public string? AppPublicKeyError { get; set; }

    /// <summary>可读的结论说明</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>沙箱原始响应（截断），便于进一步排查</summary>
    public string RawResponse { get; set; } = string.Empty;
}

/// <summary>
/// 微信 / 银行卡通道的**接线自检**结果：复用同一份结构，便于前端三通道统一渲染。
/// 与 <see cref="AlipaySelfCheckDto"/> 的区别：微信/银行卡没有「应用公钥指纹比对」这一步
/// （微信靠网关返回的 SIGN_ERROR 判定，银行卡靠网关回显的签名错误关键字判定），
/// 故去掉 ExpectedAppPublicKey 系列字段，改为更通用的 KeyFormat / MerchantId。
/// </summary>
public class ChannelSelfCheckDto
{
    /// <summary>通道标识：wechat / bank</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>通道显示名：微信支付 / 银行卡支付</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>管理员是否已打开该通道开关</summary>
    public bool Enabled { get; set; }

    /// <summary>凭证是否填写齐全（按各通道自己的必填项判定）</summary>
    public bool Configured { get; set; }

    /// <summary>网关是否接受了本次签名（true = 密钥配对正确）</summary>
    public bool SignatureAccepted { get; set; }

    /// <summary>商户标识：微信 = MchId，银行卡 = 商户号（便于管理员核对填的是不是同一家）</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>网关地址</summary>
    public string Gateway { get; set; } = string.Empty;

    /// <summary>密钥/签名格式说明：微信 = 私钥格式（PKCS#8/PKCS#1），银行卡 = 签名算法（HMAC-SHA256）</summary>
    public string KeyFormat { get; set; } = string.Empty;

    /// <summary>可读的结论说明</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>网关原始响应（脱敏 + 截断），便于进一步排查</summary>
    public string RawResponse { get; set; } = string.Empty;
}

/// <summary>单个支付通道的接线状态（用于「支付配置」页三条通道统一展示）。</summary>
public class PaymentChannelDto
{
    /// <summary>通道标识：alipay / wechat / bank</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>通道名：支付宝 / 微信支付 / 银行卡</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>是否已接入真实通道（微信/银行卡当前恒为 false，占位）</summary>
    public bool Integrated { get; init; }

    /// <summary>管理员是否已打开该通道开关</summary>
    public bool Enabled { get; init; }

    /// <summary>凭证是否填写齐全（按各通道自己的必填项判定）</summary>
    public bool Configured { get; init; }

    /// <summary>状态标签：生产 / 沙箱 / 模拟 / 未接入</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>一句话说明（给管理员看的「为什么是这个状态」）</summary>
    public string Detail { get; init; } = string.Empty;
}

/// <summary>三条支付通道的状态汇总。</summary>
public class PaymentChannelsDto
{
    public List<PaymentChannelDto> Items { get; init; } = [];
}

/// <summary>
/// 单个支付渠道的**余额**查询结果。
/// 设计要点：无论成功失败都返回同一结构，用 <see cref="Status"/> 区分，
/// 这样前端不必为「有余额/无余额/查询失败」写三套渲染分支。
/// </summary>
public class BalanceDto
{
    /// <summary>渠道标识：alipay / wechat</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>渠道显示名：企业支付宝 / 微信支付商户</summary>
    public string ChannelName { get; init; } = string.Empty;

    /// <summary>
    /// 查询状态：
    ///   ok        —— 拿到真实余额
    ///   not_configured —— 凭证不全或该渠道未启用，无法查询
    ///   timeout   —— 官方接口超时（已重试完）
    ///   error     —— 官方接口报错或返回失败
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>可用余额（元）。仅 status=ok 时有意义</summary>
    public decimal? AvailableAmount { get; set; }

    /// <summary>账户总余额（元）。支付宝区分可用与总额；微信只回可用余额，总额同可用</summary>
    public decimal? TotalAmount { get; set; }

    /// <summary>待结算/冻结金额（元）。支付宝的冻结额、微信的 pending_amount 都落这里</summary>
    public decimal? FrozenAmount { get; set; }

    /// <summary>币种，统一 CNY</summary>
    public string Currency { get; init; } = "CNY";

    /// <summary>结果来源：remote（本次打官方接口）/ cache（命中缓存）/ fallback（降级兜底）</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>数据时间（缓存命中时是缓存生成时刻，不是本次请求时刻）</summary>
    public DateTime? FetchedAt { get; set; }

    /// <summary>缓存剩余有效期（秒）。0 表示已过期或未缓存</summary>
    public int CacheTtlSeconds { get; set; }

    /// <summary>本轮实际重试次数（用于观察官方接口是否不稳定）</summary>
    public int RetryCount { get; set; }

    /// <summary>给管理员看的可读说明（不含任何凭证）</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>余额面板汇总：两个渠道 + 本地账目对照。</summary>
public class BalanceOverviewDto
{
    public List<BalanceDto> Items { get; init; } = [];

    /// <summary>
    /// 本地已收金额（payment_record 中「已支付」的合计）。
    /// 与官方余额并排展示，方便管理员做粗对账——两者差异通常来自手续费、退款、其他收入，
    /// 但若差异巨大就说明有账没对上。
    /// </summary>
    public decimal LocalPaidTotal { get; init; }

    /// <summary>本地待收款（未支付订单合计）</summary>
    public decimal LocalUnpaidTotal { get; init; }

    /// <summary>缓存配置（供前端显示"5 分钟内不重复请求"）</summary>
    public int CacheMinutes { get; init; }

    /// <summary>是否本次强制刷新（跳过了缓存）</summary>
    public bool Forced { get; init; }
}

/// <summary>管理员端「支付配置」回显：密钥类字段一律脱敏，只回显首尾几位。</summary>
public class PayConfigViewDto
{
    /// <summary>当前生效模式：生产 / 沙箱 / 模拟</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>当前模式的一句话说明（为什么是这一级）</summary>
    public string ModeReason { get; set; } = string.Empty;

    // ── 生产环境（脱敏回显） ──
    public bool ProdEnabled { get; init; }
    public string ProdAppId { get; init; } = string.Empty;
    /// <summary>支付宝会员 ID（PID，16 位数字）。非密钥，明文回显——余额查询必填。</summary>
    public string ProdPid { get; init; } = string.Empty;
    public string ProdPrivateKeyMasked { get; init; } = string.Empty;
    public string ProdAlipayPublicKeyMasked { get; init; } = string.Empty;
    public string ProdGateway { get; init; } = string.Empty;
    public bool ProdPrivateKeySet { get; init; }
    public bool ProdAlipayPublicKeySet { get; init; }

    // ── 沙箱环境（脱敏回显） ──
    public string SandboxAppId { get; init; } = string.Empty;
    public string SandboxPid { get; init; } = string.Empty;
    public string SandboxPrivateKeyMasked { get; init; } = string.Empty;
    public string SandboxAlipayPublicKeyMasked { get; init; } = string.Empty;
    public string SandboxGateway { get; init; } = string.Empty;
    public bool SandboxPrivateKeySet { get; init; }
    public bool SandboxAlipayPublicKeySet { get; init; }

    // ── 微信支付（正式通道：WechatPayService，APIv3） ──
    public bool WechatEnabled { get; init; }
    public string WechatAppId { get; init; } = string.Empty;
    public string WechatMchId { get; init; } = string.Empty;
    public string WechatApiV3KeyMasked { get; init; } = string.Empty;
    public string WechatCertSerialMasked { get; init; } = string.Empty;
    public string WechatPrivateKeyMasked { get; init; } = string.Empty;
    public string WechatNotifyUrl { get; init; } = string.Empty;
    public string WechatGateway { get; init; } = string.Empty;
    /// <summary>微信支付公钥（验回调签名，脱敏回显）</summary>
    public string WechatPlatformKeyMasked { get; init; } = string.Empty;
    /// <summary>下单方式：h5 = 跳转 H5 收银台（企业主体 + 备案域名）；native = 返回二维码让用户扫</summary>
    public string WechatPayMode { get; init; } = string.Empty;
    public bool WechatApiV3KeySet { get; init; }
    public bool WechatPrivateKeySet { get; init; }

    // ── 银行卡支付（正式通道：BankPayService，通用收银网关） ──
    public bool BankEnabled { get; init; }
    public string BankMerchantNo { get; init; } = string.Empty;
    public string BankGateway { get; init; } = string.Empty;
    public string BankSignKeyMasked { get; init; } = string.Empty;
    public string BankNotifyUrl { get; init; } = string.Empty;
    /// <summary>退款地址（非必填，留空时按支付网关推导）</summary>
    public string BankRefundUrl { get; init; } = string.Empty;
    /// <summary>查单地址（非必填，留空时按支付网关推导：/pay → /query）</summary>
    public string BankQueryUrl { get; init; } = string.Empty;
    /// <summary>余额查询地址（非必填，留空时按支付网关推导：/pay → /balance）</summary>
    public string BankBalanceUrl { get; init; } = string.Empty;
    public bool BankSignKeySet { get; init; }

    // ── 公共 ──
    public string NotifyUrl { get; init; } = string.Empty;
    public string ReturnUrl { get; init; } = string.Empty;

    /// <summary>
    /// 库里**没有**显式配置回调地址，当前展示的是自动兜底值（按本机网卡/当前访问源推算）。
    /// 前端据此把输入框留空并只做提示——否则管理员看到"有值"一点保存，
    /// 就把某个临时 IP 固化进 system_config，换网络后再也回不来。
    /// </summary>
    public bool NotifyUrlAuto { get; init; }

    /// <summary>同步跳转地址是否为自动兜底值（语义同上）。</summary>
    public bool ReturnUrlAuto { get; init; }

    /// <summary>
    /// 各密钥项的**来源**（键 → env / secrets / db / none），**只回来源不回值**。
    /// 供后台判断"管理员在页面填的密钥是否真的生效"——
    /// 若某项来自 env，说明部署环境已注入，页面上的改动不会生效。
    /// </summary>
    public Dictionary<string, string> SecretSources { get; init; } = new();
}

/// <summary>
/// 管理员端保存支付凭证。
/// 密钥字段留空 = 保持原值不变（避免每次保存都要重贴一大串私钥），
/// 传 "CLEAR" 则显式清空该字段。
/// </summary>
public class SavePayConfigDto
{
    public bool? ProdEnabled { get; init; }

    public string? ProdAppId { get; init; }
    public string? ProdPid { get; init; }
    public string? ProdPrivateKey { get; init; }
    public string? ProdAlipayPublicKey { get; init; }
    public string? ProdGateway { get; init; }

    public string? SandboxAppId { get; init; }
    public string? SandboxPid { get; init; }
    public string? SandboxPrivateKey { get; init; }
    public string? SandboxAlipayPublicKey { get; init; }
    public string? SandboxGateway { get; init; }

    // ── 微信支付（正式通道） ──
    public bool? WechatEnabled { get; init; }
    public string? WechatAppId { get; init; }
    public string? WechatMchId { get; init; }
    public string? WechatApiV3Key { get; init; }
    public string? WechatCertSerial { get; init; }
    public string? WechatPrivateKey { get; init; }
    public string? WechatNotifyUrl { get; init; }
    public string? WechatGateway { get; init; }
    public string? WechatPlatformKey { get; init; }
    /// <summary>下单方式：h5 / native。留空表示不修改（旧版前端不传时不会覆盖已选值）</summary>
    public string? WechatPayMode { get; init; }

    // ── 银行卡支付（正式通道） ──
    public bool? BankEnabled { get; init; }
    public string? BankMerchantNo { get; init; }
    public string? BankGateway { get; init; }
    public string? BankSignKey { get; init; }
    public string? BankNotifyUrl { get; init; }
    public string? BankRefundUrl { get; init; }
    public string? BankQueryUrl { get; init; }
    public string? BankBalanceUrl { get; init; }

    public string? NotifyUrl { get; init; }
    public string? ReturnUrl { get; init; }
}

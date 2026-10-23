using System.Security.Claims;
using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using ERental.Services;
using ERental.Authorization;
using ERental.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERental.Controllers;

[ApiController]
[Route("api/[controller]")]
// 支付记录查询：管理员 / 财务（对账）；退款由前台在归还结算环节发起；
// 发起支付 / 查支付状态：顾客为自己的订单付款，故对顾客开放（方法内校验归属）。
[Authorize(Policy = Policies.AnyRole)]
public class PaymentController(
    PaymentService svc, AlipayService alipay, WechatPayService wechat, BankPayService bank,
    PayConfigService payConfig, BalanceService balance, OrderService orders,
    LogService logs, MemberService member, AppDbContext db, ILogger<PaymentController> logger) : ControllerBase
{
    private readonly PaymentService _svc = svc;
    private readonly AlipayService _alipay = alipay;
    private readonly WechatPayService _wechat = wechat;
    private readonly BankPayService _bank = bank;
    private readonly PayConfigService _payConfig = payConfig;
    private readonly BalanceService _balance = balance;
    private readonly OrderService _orders = orders;
    private readonly LogService _logs = logs;
    private readonly MemberService _member = member;
    private readonly AppDbContext _db = db;
    private readonly ILogger<PaymentController> _logger = logger;

    private bool IsCustomer => User.IsInRole(AuthService.RoleCustomer);
    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    /// <summary>
    /// 匿名端点（支付回调）取不到操作人。
    /// 这里**不能**沿用 CurrentUserId——它对匿名请求会拿 null 再 int.Parse 直接抛
    /// NullReferenceException（新增 mock/notify 端点时踩过）。
    /// system_log.user_id 本就可空，故安全返回 null 即可。
    /// </summary>
    private int? CurrentUserIdOrNull =>
        int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>
    /// 支付完成后引导用户返回的前端地址（微信 H5 的 redirect_url 等）。
    /// 复用支付配置里的 return_url 兜底逻辑（局域网模式自动换本机 IP 与当前前端端口），
    /// 并带上订单号与渠道，让 /pay-result 能按渠道渲染。
    /// </summary>
    private async Task<string> BuildReturnUrl(string orderNo, string payType)
    {
        var raw = await _payConfig.GetRawAsync();
        var configured = raw.TryGetValue(PayConfigService.KeyReturnUrl, out var v) ? v.Trim() : "";
        var baseUrl = _payConfig.FallbackChannelUrl(configured, "pay-result");
        return $"{baseUrl}{(baseUrl.Contains('?') ? "&" : "?")}orderNo={Uri.EscapeDataString(orderNo)}&payType={Uri.EscapeDataString(payType)}";
    }

    /// <summary>
    /// 管理员操作审计：记录「谁、何时、调了哪个接口、结果如何」（需求 4.2.1 操作留痕）。
    /// 与业务日志共用 system_log 表，模块统一为「支付管理」便于按模块检索。
    /// 注意：审计内容**只记动作与结果，不记任何凭证值**。
    /// </summary>
    /// <param name="action">动作描述（如「发起微信支付」）</param>
    /// <param name="ok">操作结果是否成功（决定日志结果的「成功 / 失败」）</param>
    /// <param name="detail">补充说明（可为 null，仅记业务信息，不记凭证值）</param>
    /// <param name="actorId">操作人；匿名回调传 null（<see cref="CurrentUserIdOrNull"/>）</param>
    private Task AuditAsync(string action, bool ok, string? detail = null, int? actorId = null)
    {
        var content = detail == null ? action : $"{action}（{detail}）";
        return _logs.WriteLog(actorId ?? CurrentUserIdOrNull, content, "支付管理", ok ? "成功" : "失败", ClientIp);
    }

    /// <summary>
    /// 支付宝接线自检：一键判断当前配置能否联通。
    /// 典型用途——报 isv.invalid-signature 时，调它拿到「本地私钥对应的应用公钥」，
    /// 去开放平台核对/重新上传，避免靠"下单失败"反复试错。
    /// </summary>
    [HttpGet("alipay/selfcheck")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> AlipaySelfCheck()
    {
        try
        {
            var dto = await _alipay.SelfCheckAsync();
            await AuditAsync("支付宝接线自检", dto.SignatureAccepted || dto.Mode == PayConfigService.ModeMock,
                $"模式 {dto.Mode}，签名{(dto.SignatureAccepted ? "通过" : "未通过")}");
            return Ok(new ApiResponse<AlipaySelfCheckDto> { Data = dto });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("支付宝自检异常（{ExType}）", ex.GetType().Name);
            await AuditAsync("支付宝接线自检", false, "执行异常");
            return Ok(new ApiResponse<AlipaySelfCheckDto>
            {
                Data = new AlipaySelfCheckDto { Message = "自检执行异常，请查看服务端日志" }
            });
        }
    }

    /// <summary>
    /// 三条通道（支付宝/微信/银行卡）的接线状态汇总。
    /// 前端用同一张表渲染三条通道的「已配置 / 未接入」状态，避免每个通道各写一份状态逻辑。
    /// </summary>
    [HttpGet("channels")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Channels() =>
        Ok(new ApiResponse<PaymentChannelsDto> { Data = await _payConfig.GetChannelsAsync() });

    /// <summary>
    /// 账户余额：企业支付宝 + 微信支付商户，并附本地账目对照。
    ///
    /// 安全与稳定性：
    ///  - **仅管理员**（`[Authorize(Roles="管理员")]`）；
    ///  - 写入操作审计日志（操作人 / 时间 / 接口 / 结果）；
    ///  - 查询带 8 秒超时、2 次重试、5 分钟缓存与降级兜底（见 <see cref="BalanceService"/>），
    ///    因此本接口**不会因官方接口抖动而抛异常**，永远返回可渲染的结构。
    /// </summary>
    /// <param name="force">true = 跳过缓存强制刷新（管理员点「刷新余额」时用）</param>
    [HttpGet("balances")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Balances([FromQuery] bool force = false)
    {
        try
        {
            var data = await _balance.GetOverviewAsync(force);
            var okCount = data.Items.Count(i => i.Status == "ok");
            await AuditAsync(force ? "强制刷新支付账户余额" : "查询支付账户余额",
                okCount > 0, $"成功 {okCount}/{data.Items.Count} 个渠道");
            return Ok(new ApiResponse<BalanceOverviewDto> { Data = data });
        }
        catch (Exception ex)
        {
            // BalanceService 内部已兜底，走到这里属意外（如本地账目统计失败）。
            // 只记类型、只回通用文案——不回异常细节。
            _logger.LogError("余额查询接口异常（{ExType}）", ex.GetType().Name);
            await AuditAsync("查询支付账户余额", false, "执行异常");
            return Ok(new ApiResponse<BalanceOverviewDto>
            {
                Data = new BalanceOverviewDto { CacheMinutes = BalanceService.CacheMinutes, Forced = force },
                Message = "余额查询暂时不可用，请稍后重试"
            });
        }
    }


    /// <summary>
    /// 读取支付配置（管理员端回显）。密钥类字段一律脱敏，绝不把明文私钥下发到浏览器。
    /// </summary>
    [HttpGet("config")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> GetPayConfig() =>
        Ok(new ApiResponse<PayConfigViewDto> { Data = await _payConfig.GetViewAsync() });

    /// <summary>
    /// 保存支付配置。管理员填写生产/沙箱凭证与回调地址，保存后立即生效（无需重启）。
    /// 密钥字段留空表示"保持不变"；填 CLEAR 表示清空。
    /// </summary>
    [HttpPut("config")]
    [Authorize(Policy = Policies.AdminOnly)]
    [SkipAudit]   // 本方法内已有 AuditAsync，会记录生效模式；自动审计再记一条属重复
    public async Task<IActionResult> SavePayConfig([FromBody] SavePayConfigDto dto)
    {
        try
        {
            await _payConfig.SaveViewAsync(dto);
            // 凭证可能已换账户，旧的余额缓存不再代表同一账户，必须失效
            BalanceService.InvalidateCache();
            var view = await _payConfig.GetViewAsync();
            // 审计只记"改了哪些字段名"，**绝不记字段值**（那里面是密钥）
            await AuditAsync("保存支付配置", true, $"生效模式 {view.Mode}");
            return Ok(new ApiResponse<PayConfigViewDto>
            {
                Data = view,
                Message = $"支付配置已保存，当前生效模式：{view.Mode}"
            });
        }
        catch (Exception ex)
        {
            // 保存链路会经手密钥，异常消息先用 SecretMasker 过一遍再下发：
            // 万一某个底层异常把传入的值带进 Message，也不会泄漏出去。
            // 同时把原始异常类型记进服务端日志（不回给前端）。
            _logger.LogWarning("保存支付配置失败（{ExType}）", ex.GetType().Name);
            await AuditAsync("保存支付配置", false, "保存失败");
            // 先按统一口径翻译（业务中文原样 / 驱动原文转中文），再对保留下来的细节做密钥脱敏。
            var safe = SecretMasker.Scrub(ErrorText.Safe(ex),
                dto.ProdPrivateKey, dto.ProdAlipayPublicKey,
                dto.SandboxPrivateKey, dto.SandboxAlipayPublicKey,
                dto.WechatApiV3Key, dto.WechatPrivateKey, dto.BankSignKey);
            return BadRequest(new ApiResponse<object> { Code = 400, Message = safe, Success = false });
        }
    }

    /// <summary>支付记录列表：可按状态 + 时间段（from/to，闭区间）筛选，供报表页区间统计使用。仅财务（资金流水属财务职责范围）</summary>
    [HttpGet]
    [Authorize(Policy = Policies.FinanceOnly)]
    public async Task<IActionResult> GetAll(string? status, DateTime? from, DateTime? to) =>
        Ok(new ApiResponse<List<PaymentDto>> { Data = await _svc.GetAll(status, from, to) });

    /// <summary>
    /// 发起支付：为订单创建支付并返回收银台地址。
    /// 已配好支付宝沙箱密钥且 Enabled=true 时返回官方收银台链接；否则返回内置模拟收银台。
    /// </summary>
    [HttpPost("create")]
    public async Task<IActionResult> Create([FromBody] CreatePayDto dto)
    {
        try
        {
            // 先回收超时未支付的订单：避免顾客拿着一个已被系统取消的单继续点支付
            await _orders.ReleaseExpiredPendingOrders();

            var order = await _db.RentalOrders.FirstOrDefaultAsync(o => o.OrderNo == dto.OrderNo)
                ?? throw new Exception("订单不存在");

            // 顾客只能为自己的订单付款：按 Token 内的 customerId 二次校验，不信任前端传参
            if (IsCustomer && order.CustomerId != CurrentUserId)
                return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "无权支付该订单", Success = false });

            if (order.OrderStatus is "已支付" or "租赁中")
            {
                var rtm = await _payConfig.ResolveAsync();
                return Ok(new ApiResponse<CreatePayResultDto>
                {
                    Data = new CreatePayResultDto
                    {
                        OrderNo = order.OrderNo,
                        PayType = "支付宝",
                        Amount = order.TotalRent + order.Deposit,
                        PayUrl = "",
                        RealPay = rtm is { IsReal: true, CredentialsComplete: true }
                    },
                    Message = "订单已支付，无需重复支付"
                });
            }

            if (order.OrderStatus != "待支付")
                throw new Exception($"当前订单状态为「{order.OrderStatus}」，不可支付");
            var amount = order.TotalRent + order.Deposit;
            var payType = string.IsNullOrWhiteSpace(dto.PayType) ? "支付宝" : dto.PayType.Trim();

            // 发起支付即把支付记录的渠道从下单占位（「待定」）更新为顾客本次所选；
            // 付款完成前员工侧看到的也是真实意向，入账时仍会以回调/查单数据最终覆盖。
            var pendingRecords = await _db.PaymentRecords
                .Where(p => p.OrderId == order.OrderId && p.PayStatus == "待支付")
                .ToListAsync();
            foreach (var pr in pendingRecords)
                pr.PayType = payType;
            if (pendingRecords.Count > 0)
                await _db.SaveChangesAsync();

            // 当前生效模式决定是否真实支付（生产/沙箱为真实，模拟为内置收银台）
            var rt = await _payConfig.ResolveAsync();
            var realAlipay = rt is { IsReal: true, CredentialsComplete: true };

            // 按渠道路由到各自的支付服务。三个服务内部遵循同一套降级约定：
            // 凭证齐全 → 真实通道（返回官方/网关收银台地址）；不齐全 → 内置模拟收银台。
            // 返回 (payUrl, real) 的服务当场告诉你走的是哪一档，前端按 real 决定跳转方式。
            // 微信是唯一有两个真实分支的通道：h5 回跳转地址、native 回二维码内容，两者互斥，
            // 前端先看 qrCode 再决定「渲染二维码 + 轮询」还是「整页跳转」。
            string payUrl;
            string qrCode = "";
            string payMode = "";
            bool realPay;
            switch (payType)
            {
                case "支付宝":
                    payUrl = await _alipay.CreatePagePayAsync(order.OrderNo, $"电动车租赁订单 {order.OrderNo}", amount);
                    realPay = realAlipay;
                    break;

                case "微信":
                {
                    var wx = await _wechat.CreatePayAsync(
                        order.OrderNo, $"电动车租赁订单 {order.OrderNo}", amount,
                        await BuildReturnUrl(order.OrderNo, "微信"));
                    payUrl = wx.PayUrl;
                    qrCode = wx.QrCode;
                    payMode = wx.PayMode;
                    realPay = wx.Real;
                    break;
                }

                case "银行卡":
                    (payUrl, realPay) = await _bank.CreatePayAsync(
                        order.OrderNo, $"电动车租赁订单 {order.OrderNo}", amount);
                    break;

                default:
                    throw new Exception($"不支持的支付方式「{payType}」");
            }

            return Ok(new ApiResponse<CreatePayResultDto>
            {
                Data = new CreatePayResultDto
                {
                    OrderNo = order.OrderNo,
                    PayType = payType,
                    Amount = amount,
                    PayUrl = payUrl,
                    QrCode = qrCode,
                    PayMode = payMode,
                    RealPay = realPay
                }
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false });
        }
    }

    /// <summary>
    /// 支付宝异步回调。支付宝服务器 POST 表单过来，我方验签通过才把订单置为已支付。
    /// 返回纯文本 "success"/"failure"（不是 JSON —— 支付宝只认这两个词）。
    /// </summary>
    [HttpPost("alipay/notify")]
    [AllowAnonymous]   // 回调来自支付宝服务器，无我方 JWT，必须匿名 + 靠验签保真
    public async Task<IActionResult> AlipayNotify()
    {
        try
        {
            var form = Request.Form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
            var result = await _alipay.HandleNotifyAsync(form);
            return Content(result, "text/plain");
        }
        catch (Exception ex)
        {
            // 回调处理异常不能 500：返回 failure 让支付宝按策略重推，同时留下日志。
            // 只记异常类型——回调请求体含 sign 等敏感字段，不落日志。
            _logger.LogError("支付宝回调处理异常（{ExType}）", ex.GetType().Name);
            return Content("failure", "text/plain");
        }
    }

    /// <summary>
    /// 内置模拟收银台（/mock-pay）的支付通知入口。
    ///
    /// 与 alipay/notify 的区别（这是修「微信/银行卡提示通知未受理」的关键）：
    ///   · alipay/notify —— 真实支付宝回调，**必须验签**，只回文本 success/failure；
    ///   · mock/notify   —— 模拟收银台专用，按"当前是否生产模式"判定，返回 JSON + 具体原因。
    /// 模拟通知天生没有签名，若混进真实回调通道必然被验签拒绝，故必须分家。
    /// </summary>
    [HttpPost("mock/notify")]
    [AllowAnonymous]
    public async Task<IActionResult> MockNotify()
    {
        try
        {
            var form = Request.Form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
            var r = await _alipay.HandleMockNotifyAsync(form);
            form.TryGetValue("out_trade_no", out var orderNo);
            // 端点匿名（与真实回调一样没有我方 JWT），故显式传 null 作为操作人
            await AuditAsync("模拟收银台支付通知", r.Accepted, $"订单 {orderNo}：{r.Message}");

            return Ok(new ApiResponse<MockNotifyResult>
            {
                Data = r,
                Success = r.Accepted,
                // 失败时把服务端判定的具体原因带出去，前端直接显示，
                // 不再是一句无法行动的「支付通知未成功受理，请重试」
                Message = r.Message,
                Code = r.Accepted ? 200 : 400
            });
        }
        catch (Exception ex)
        {
            _logger.LogError("模拟支付通知处理异常（{ExType}）", ex.GetType().Name);
            return Ok(new ApiResponse<MockNotifyResult>
            {
                Data = MockNotifyResult.Fail("服务端处理支付通知时发生异常，请稍后重试。"),
                Success = false,
                Message = "服务端处理支付通知时发生异常，请稍后重试。",
                Code = 500
            });
        }
    }

    /// <summary>
    /// 微信支付异步回调（APIv3，正式通道）。
    /// 微信以 JSON 报文 POST 过来，签名放在 Wechat-* 请求头里；验签 + AES-GCM 解密通过才入账。
    /// 微信对非 2xx 响应按策略重推：受理成功回 200，验签/入账失败回 4xx 让它重试。
    /// </summary>
    [HttpPost("wechat/notify")]
    [AllowAnonymous]   // 回调来自微信支付服务器，无我方 JWT，必须匿名 + 靠验签保真
    public async Task<IActionResult> WechatNotify()
    {
        // 验签必须基于**原始报文**：任何中间反序列化都会改变字节序，导致签名对不上
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();

        var headers = new Dictionary<string, string>();
        foreach (var h in new[] { "Wechat-Timestamp", "Wechat-Nonce", "Wechat-Signature", "Wechat-Serial" })
            if (Request.Headers.TryGetValue(h, out var v))
                headers[h] = v.ToString();

        try
        {
            var (ok, msg) = await _wechat.HandleNotifyAsync(headers, body);
            // 订单号在解密后的报文里，这里拿不到明文订单号；审计落在服务端日志
            // （HandleNotifyAsync 内部已按订单号记录入账结果），端点侧不重复记。
            return ok
                ? Ok(new { code = "SUCCESS", message = "成功" })
                : BadRequest(new { code = "FAIL", message = msg });
        }
        catch (Exception ex)
        {
            // 回调处理异常不能把细节外泄：回 4xx 让微信按策略重推，日志只记异常类型
            _logger.LogError("微信回调处理异常（{ExType}）", ex.GetType().Name);
            return BadRequest(new { code = "FAIL", message = "处理异常" });
        }
    }

    /// <summary>
    /// 银行卡收银网关异步回调（正式通道）。
    /// 网关以表单 POST 过来，按同一套 HMAC-SHA256 规则验签后入账。
    /// 返回纯文本 "success"/"failure"（业内网关普遍认这两个词，与支付宝回调同约定）。
    /// </summary>
    [HttpPost("bank/notify")]
    [AllowAnonymous]
    public async Task<IActionResult> BankNotify()
    {
        try
        {
            var form = Request.Form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
            var (ok, msg) = await _bank.HandleNotifyAsync(form);
            form.TryGetValue("out_trade_no", out var orderNo);
            await AuditAsync("银行卡回调", ok, $"订单 {orderNo}：{msg}");
            return Content(ok ? "success" : "failure", "text/plain");
        }
        catch (Exception ex)
        {
            _logger.LogError("银行卡回调处理异常（{ExType}）", ex.GetType().Name);
            return Content("failure", "text/plain");
        }
    }

    /// <summary>查询支付状态：前端支付页轮询，或从支付渠道回跳后确认最终状态</summary>
    /// <remarks>
    /// 匿名可访问：支付宝同步回跳时用户可能没有我方登录态（回跳页路由即为免登录设计），
    /// 订单号本身是不可猜测的临时凭证，且响应只含状态/金额/交易号，不含客户隐私。
    /// 已登录顾客仍校验订单归属；匿名请求靠订单号定位，无法枚举越权。
    /// HY 开头为会员购买交易号，走会员分支（无租赁订单）。
    /// </remarks>
    [HttpGet("{orderNo}/status")]
    [AllowAnonymous]
    public async Task<IActionResult> Status(string orderNo)
    {
        // ── 会员购买（HY 交易号）：查单兜底 + 幂等入账，口径与订单一致 ──
        if (orderNo.StartsWith("HY"))
            return await MemberPayStatus(orderNo);

        var order = await _db.RentalOrders.FirstOrDefaultAsync(o => o.OrderNo == orderNo);
        if (order == null) return NotFound(new ApiResponse<object> { Code = 404, Message = "订单不存在", Success = false });

        if (IsCustomer && order.CustomerId != CurrentUserId)
            return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "无权查看该订单", Success = false });

        // 若本地仍为待支付，向支付渠道主动查一次（应对回调延迟/丢包——沙箱/开发环境
        // 回调常打不进本机，查单兜底会当场入账，所以查完必须重载订单，
        // 否则前端轮询永远拿到旧状态）。渠道看支付记录最近一次的 payType：
        // 模拟收银台的入账由 mock/notify 主动推送，无需查单。
        if (order.OrderStatus == "待支付")
        {
            var latestType = (await _db.PaymentRecords.AsNoTracking()
                .Where(p => p.OrderId == order.OrderId)
                .OrderByDescending(p => p.PayId).FirstOrDefaultAsync())?.PayType ?? "";

            if (latestType == "微信")
                await _wechat.QueryTradeStatusAsync(orderNo);
            else
                await _alipay.QueryTradeStatusAsync(orderNo);
            await _db.Entry(order).ReloadAsync();
        }

        var record = await _db.PaymentRecords.AsNoTracking()
            .Where(p => p.OrderId == order.OrderId && p.PayStatus == "已支付")
            .OrderByDescending(p => p.PayId).FirstOrDefaultAsync();

        var paid = order.OrderStatus is "已支付" or "租赁中";
        return Ok(new ApiResponse<PayStatusDto>
        {
            Data = new PayStatusDto
            {
                OrderNo = order.OrderNo,
                OrderStatus = order.OrderStatus,
                Paid = paid,
                Amount = order.TotalRent + order.Deposit,
                TransactionNo = record?.TransactionNo ?? ""
            }
        });
    }

    /// <summary>
    /// 会员购买状态查询（HY 交易号）：读取 memberpay.* 暂存意向；仍待支付且为真实模式时
    /// 主动向支付宝查单，确认成功当场入账（与订单查单兜底同思路——沙箱环境 notify 进不来）。
    /// 已完成标记 / 过期 / 不存在分别返回对应状态，轮询端按 paid 判定终止。
    /// </summary>
    private async Task<IActionResult> MemberPayStatus(string tradeNo)
    {
        var view = await _member.GetPurchaseView(tradeNo);
        if (!view.Exists)
            return NotFound(new ApiResponse<object> { Code = 404, Message = "会员购买记录不存在", Success = false });

        var paid = view.Done;
        var statusText = view.Done ? "已支付" : "待支付";
        var alipayNo = view.AlipayTradeNo;

        if (!paid && DateTime.Now - view.CreateTime > TimeSpan.FromHours(MemberService.PendingExpireHours))
            statusText = "已过期";

        if (!paid && statusText == "待支付")
        {
            try
            {
                var (_, tradeStatus, tNo, totalAmount) = await _alipay.QueryTradeRawAsync(tradeNo);
                if (tradeStatus is "TRADE_SUCCESS" or "TRADE_FINISHED")
                {
                    var paidAmt = decimal.TryParse(totalAmount, out var pa) ? pa : 0m;
                    var (ok, msg, _, no) = await _member.ApplyPaidPurchaseAsync(tradeNo, tNo, "支付宝", paidAmt);
                    if (ok) { paid = true; statusText = "已支付"; alipayNo = no; }
                    else if (msg.Contains("过期")) statusText = "已过期";
                    _logger.LogInformation("会员购买 {TradeNo} 查单兜底入账：{Result}（{Message}）", tradeNo, ok ? "成功" : "拒绝", msg);
                }
            }
            catch (Exception ex)
            {
                // 查单失败不打断轮询，回退暂存状态
                _logger.LogWarning("查询会员购买 {TradeNo} 状态失败（{ExType}）", tradeNo, ex.GetType().Name);
            }
        }

        return Ok(new ApiResponse<PayStatusDto>
        {
            Data = new PayStatusDto
            {
                OrderNo = tradeNo,
                OrderStatus = statusText,
                Paid = paid,
                Amount = view.Amount,
                TransactionNo = alipayNo
            }
        });
    }

    [HttpPut("{id:int}/refund")]
    [Authorize(Policy = Policies.ReceptionOrFinance)]
    public async Task<IActionResult> Refund(int id, [FromBody] decimal amount)
    {
        try
        {
            // 返回值区分「真的原路退回」与「仅账面登记」，前台/财务必须看得见差别，
            // 否则会以为模拟通道的退款也已到账。
            var r = await _svc.Refund(id, amount);
            return Ok(new ApiResponse<object> { Message = r.Message, Data = r });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }
}

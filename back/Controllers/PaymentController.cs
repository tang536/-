using System.Net;
using System.Security.Claims;
using ERental.DTOs;
using ERental.Models;
using ERental.Services;
using ERental.Authorization;
using ERental.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ERental.Controllers;

[ApiController]
[Route("api/[controller]")]
//支付记录查询：管理员/财务（对账）
[Authorize(Policy = Policies.AnyRole)]
public class PaymentController(PaymentService svc, AlipayService alipay, WechatPayService wechat, BankPayService bank, PayConfigService payConfig, BalanceService balance, OrderService orders, LogService logs, MemberService member, ILogger<PaymentController> logger) : ControllerBase
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
    private readonly ILogger<PaymentController> _logger = logger;

    ///<summary>支付渠道回调端点约定的响应类型：支付宝/微信/银行卡都只认纯文本</summary>
    private const string ContentTypeText = "text/plain";

    private bool IsCustomer => User.IsInRole(AuthService.RoleCustomer);
    ///<summary>匿名回跳（支付宝同步返回时可能没有我方登录态）：只回状态，不回金额与网关交易号</summary>
    private bool IsAnonymous => User.Identity?.IsAuthenticated != true;
    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    ///<summary>匿名端点（支付回调）取不到操作人</summary>
    private int? CurrentUserIdOrNull => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    ///<summary>浏览器此刻的地址栏源：优先 X-Client-Origin（前端拦截器注入）</summary>
    private string? ClientOrigin => PayConfigService.ResolveClientOrigin(Request.Headers["X-Client-Origin"].ToString(), Request.Headers.Referer.ToString());//NOSONAR 访问源只能从原始请求头读取，Header/Referer 无法用模型绑定，且这里只是取值不做信任判断

    ///<summary>支付完成后引导用户返回的前端地址（微信 H5 的 redirect_url、支付宝 return_url 等）</summary>
    private async Task<string> BuildReturnUrl(string orderNo, string payType)
    {
        var baseUrl = await _payConfig.BuildReturnUrlAsync(ClientOrigin, "pay-result");
        //拿不到地址就返回空
        if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;
        return $"{baseUrl}{(baseUrl.Contains('?') ? "&" : "?")}orderNo={Uri.EscapeDataString(orderNo)}&payType={Uri.EscapeDataString(payType)}";
    }

    ///<summary>管理员操作审计：记录「谁、何时、调了哪个接口、结果如何」</summary>
    private Task AuditAsync(string action, bool ok, string? detail = null, int? actorId = null)
    {
        var content = detail == null ? action : $"{action}（{detail}）";
        return _logs.WriteLog(actorId ?? CurrentUserIdOrNull, content, "支付管理", ok ? "成功" : "失败", ClientIp);
    }

    ///<summary>自检审计文案</summary>
    private static string DescribeSelfCheck(bool signatureAccepted, bool enabled)
    {
        if (signatureAccepted) return "签名通过";
        if (enabled) return "签名未通过";
        return "未启用/未配置";
    }

    ///<summary>支付宝接线自检：一键判断当前配置能否联通</summary>
    [HttpGet("alipay/selfcheck")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> CheckPayChannel()
    {
        try
        {
            var dto = await _alipay.SelfCheckAsync();
            await AuditAsync("支付宝接线自检", dto.SignatureAccepted || dto.Mode == PayConfigService.ModeMock, $"模式 {dto.Mode}，签名{(dto.SignatureAccepted ? "通过" : "未通过")}");
            return Ok(new ApiResponse<AlipaySelfCheckDto> { Data = dto });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "支付宝自检异常（{ExType}）", ex.GetType().Name);
            await AuditAsync("支付宝接线自检", false, "执行异常");
            return Ok(new ApiResponse<AlipaySelfCheckDto>
            {
                Data = new AlipaySelfCheckDto { Message = "自检执行异常，请查看服务端日志" }
            });
        }
    }

    ///<summary>微信支付接线自检：向网关发一次最小查询</summary>
    [HttpGet("wechat/selfcheck")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> CheckWechatChannel()
    {
        try
        {
            var dto = await _wechat.SelfCheckAsync();
            var ok = dto.SignatureAccepted || !dto.Enabled;
            await AuditAsync("微信支付接线自检", ok, DescribeSelfCheck(dto.SignatureAccepted, dto.Enabled));
            return Ok(new ApiResponse<ChannelSelfCheckDto> { Data = dto });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "微信自检异常（{ExType}）", ex.GetType().Name);
            await AuditAsync("微信支付接线自检", false, "执行异常");
            return Ok(new ApiResponse<ChannelSelfCheckDto>
            {
                Data = new ChannelSelfCheckDto { Channel = "wechat", ChannelName = "微信支付", Message = "自检执行异常，请查看服务端日志" }
            });
        }
    }

    ///<summary>银行卡支付接线自检：向收单网关发一次同构签名探测</summary>
    [HttpGet("bank/selfcheck")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> CheckBankChannel()
    {
        try
        {
            var dto = await _bank.SelfCheckAsync();
            var ok = dto.SignatureAccepted || !dto.Enabled;
            await AuditAsync("银行卡支付接线自检", ok, DescribeSelfCheck(dto.SignatureAccepted, dto.Enabled));
            return Ok(new ApiResponse<ChannelSelfCheckDto> { Data = dto });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "银行卡自检异常（{ExType}）", ex.GetType().Name);
            await AuditAsync("银行卡支付接线自检", false, "执行异常");
            return Ok(new ApiResponse<ChannelSelfCheckDto>
            {
                Data = new ChannelSelfCheckDto { Channel = "bank", ChannelName = "银行卡支付", Message = "自检执行异常，请查看服务端日志" }
            });
        }
    }

    ///<summary>三条通道（支付宝/微信/银行卡）的接线状态汇总</summary>
    [HttpGet("channels")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> GetPayChannels() => Ok(new ApiResponse<PaymentChannelsDto> { Data = await _payConfig.GetChannelsAsync() });

    ///<summary>账户余额：企业支付宝+微信支付商户，并附本地账目对照</summary>
    [HttpGet("balances")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> GetPayBalances([FromQuery] bool force = false)
    {
        try
        {
            var data = await _balance.GetOverviewAsync(force);
            var okCount = data.Items.Count(i => i.Status == "ok");
            await AuditAsync(force ? "强制刷新支付账户余额" : "查询支付账户余额", okCount > 0, $"成功 {okCount}/{data.Items.Count} 个渠道");
            return Ok(new ApiResponse<BalanceOverviewDto> { Data = data });
        }
        catch (Exception ex)
        {
            //BalanceService 内部已兜底，走到这里属意外（如本地账目统计失败）
            _logger.LogError(ex, "余额查询接口异常（{ExType}）", ex.GetType().Name);
            await AuditAsync("查询支付账户余额", false, "执行异常");
            return Ok(new ApiResponse<BalanceOverviewDto>
            {
                Data = new BalanceOverviewDto { CacheMinutes = BalanceService.CacheMinutes, Forced = force },
                Message = "余额查询暂时不可用，请稍后重试"
            });
        }
    }

    ///<summary>读取支付配置（管理员端回显）</summary>
    [HttpGet("config")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> GetPayConfig() => Ok(new ApiResponse<PayConfigViewDto> { Data = await _payConfig.GetViewAsync() });

    ///<summary>按需读取单个密钥的明文：管理端「支付配置」弹窗里点「显示」时调用</summary>
    [HttpGet("config/secret")]
    [Authorize(Policy = Policies.AdminOnly)]
    [SkipAudit]
    public async Task<IActionResult> GetPayConfigSecret([FromQuery] string field)
    {
        var value = await _payConfig.GetSecretAsync(field);
        if (value is null) return BadRequest(new ApiResponse<object> { Code = 400, Message = "不支持的配置项", Success = false });

        await AuditAsync("查看支付密钥明文", true, $"字段 {field}");
        return Ok(new ApiResponse<object> { Data = new { field, value } });
    }

    ///<summary>保存支付配置</summary>
    [HttpPut("config")]
    [Authorize(Policy = Policies.AdminOnly)]
    [SkipAudit]   //本方法内已有 AuditAsync，会记录生效模式；自动审计再记一条属重复
    public async Task<IActionResult> SavePayConfig([FromBody] SavePayConfigDto dto)
    {
        try
        {
            await _payConfig.SaveViewAsync(dto);
            //凭证可能已换账户，旧的余额缓存不再代表同一账户，必须失效
            BalanceService.InvalidateCache();
            var view = await _payConfig.GetViewAsync();
            //审计只记"改了哪些字段名"，绝不记字段值（那里面是密钥）
            await AuditAsync("保存支付配置", true, $"生效模式 {view.Mode}");
            return Ok(new ApiResponse<PayConfigViewDto>
            {
                Data = view,
                Message = $"支付配置已保存，当前生效模式：{view.Mode}"
            });
        }
        catch (Exception ex)
        {
            //保存链路会经手密钥
            _logger.LogWarning(ex, "保存支付配置失败（{ExType}）", ex.GetType().Name);
            await AuditAsync("保存支付配置", false, "保存失败");
            //先按统一口径翻译（业务中文原样/驱动原文转中文），再对保留下来的细节做密钥脱敏
            var safe = SecretMasker.Scrub(ErrorText.Safe(ex), dto.ProdPrivateKey, dto.ProdAlipayPublicKey, dto.SandboxPrivateKey, dto.SandboxAlipayPublicKey, dto.WechatApiV3Key, dto.WechatPrivateKey, dto.BankSignKey);
            return BadRequest(new ApiResponse<object> { Code = 400, Message = safe, Success = false });
        }
    }

    ///<summary>支付记录列表：可按状态+时间段（from/to，闭区间）筛选，供报表页区间统计使用</summary>
    [HttpGet]
    [Authorize(Policy = Policies.FinanceOnly)]
    public async Task<IActionResult> QueryPaymentRecords(string? status, DateTime? from, DateTime? to)
    {
        try
        {
            //读接口对"连接层瞬时故障"整段重试
            var data = await DbConcurrency.RetryTransientAsync(() => _svc.GetAll(status, from, to), _logger, "支付流水列表");
            return Ok(new ApiResponse<List<PaymentDto>> { Data = data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "支付流水列表查询失败（{ExType}）", ex.GetType().Name);
            return StatusCode(500, new ApiResponse<List<PaymentDto>> { Code = 500, Success = false, Message = ErrorText.Safe(ex), Data = null });
        }
    }

    ///<summary>发起支付：为订单创建支付并返回收银台地址</summary>
    [HttpPost("create")]
    public async Task<IActionResult> CreatePayment([FromBody] CreatePayDto dto)
    {
        try
        {
            //先回收超时未支付的订单
            await _orders.ReleaseExpiredPendingOrders();

            var order = await _svc.FindOrderAsync(dto.OrderNo) ?? throw new BusinessException("订单不存在");

            //顾客只能为自己的订单付款
            if (IsCustomer && order.CustomerId != CurrentUserId) return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "无权支付该订单", Success = false });

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

            if (order.OrderStatus != "待支付") throw new BusinessException($"当前订单状态为「{order.OrderStatus}」，不可支付");
            var amount = order.TotalRent + order.Deposit;
            var payType = string.IsNullOrWhiteSpace(dto.PayType) ? "支付宝" : dto.PayType.Trim();

            //发起支付即把支付记录的渠道从下单占位（「待定」）更新为顾客本次所选
            await _svc.SetPendingChannelAsync(order.OrderId, payType);

            //当前生效模式决定是否真实支付（生产/沙箱为真实，模拟为内置收银台）
            var rt = await _payConfig.ResolveAsync();
            var realAlipay = rt is { IsReal: true, CredentialsComplete: true };

            //按渠道路由到各自的支付服务
            string payUrl;
            string qrCode = "";
            string payMode = "";
            bool realPay;
            switch (payType)
            {
                case "支付宝":
                    //return_url/notify_url 都必须按用户当前访问源生成：写死配置值时
                    payUrl = await _alipay.CreatePagePayAsync(
                        order.OrderNo, $"电动车租赁订单 {order.OrderNo}", amount,
                        await BuildReturnUrl(order.OrderNo, "支付宝"),
                        await _payConfig.BuildNotifyUrlAsync(ClientOrigin, "api/payment/alipay/notify"));
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
                    throw new BusinessException($"不支持的支付方式「{payType}」");
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
        catch (Exception ex) when (ErrorText.IsHandled(ex))
        {
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false });
        }
    }

    ///<summary>支付宝异步回调</summary>
    [HttpPost("alipay/notify")]
    [AllowAnonymous]   //回调来自支付宝服务器，无我方 JWT，必须匿名+靠验签保真
    public async Task<IActionResult> AlipayNotify()
    {
        try
        {
            var form = Request.Form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
            var result = await _alipay.HandleNotifyAsync(form);
            return Content(result, ContentTypeText);
        }
        catch (Exception ex)
        {
            //回调处理异常不能 500：返回 failure 让支付宝按策略重推，同时留下日志
            _logger.LogError(ex, "支付宝回调处理异常（{ExType}）", ex.GetType().Name);
            return Content("failure", ContentTypeText);
        }
    }

    ///<summary>内置模拟收银台（/mock-pay）的支付通知入口</summary>
    [HttpPost("mock/notify")]
    [AllowAnonymous]
    public async Task<IActionResult> MockNotify()
    {
        try
        {
            var form = Request.Form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
            //端点匿名，模拟通知只允许本机发起
            var remoteIp = HttpContext.Connection.RemoteIpAddress;
            var isLoopback = remoteIp is not null && IPAddress.IsLoopback(remoteIp);
            var r = await _alipay.HandleMockNotifyAsync(form, isLoopback);
            form.TryGetValue("out_trade_no", out var orderNo);
            //端点匿名（与真实回调一样没有我方 JWT），故显式传 null 作为操作人
            await AuditAsync("模拟收银台支付通知", r.Accepted, $"订单 {orderNo}：{r.Message}");

            return Ok(new ApiResponse<MockNotifyResult>
            {
                Data = r,
                Success = r.Accepted,
                //失败时把服务端判定的具体原因带出去，前端直接显示，用户才知道下一步该补什么
                Message = r.Message,
                Code = r.Accepted ? 200 : 400
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "模拟支付通知处理异常（{ExType}）", ex.GetType().Name);
            return Ok(new ApiResponse<MockNotifyResult>
            {
                Data = MockNotifyResult.Fail("服务端处理支付通知时发生异常，请稍后重试。"),
                Success = false,
                Message = "服务端处理支付通知时发生异常，请稍后重试。",
                Code = 500
            });
        }
    }

    ///<summary>微信支付异步回调（APIv3，正式通道）</summary>
    [HttpPost("wechat/notify")]
    [AllowAnonymous]   //回调来自微信支付服务器，无我方 JWT，必须匿名+靠验签保真
    public async Task<IActionResult> WechatNotify()
    {
        //验签必须基于原始报文
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();

        var headers = new Dictionary<string, string>();
        foreach (var h in new[] { "Wechat-Timestamp", "Wechat-Nonce", "Wechat-Signature", "Wechat-Serial" })
            if (Request.Headers.TryGetValue(h, out var v))
                headers[h] = v.ToString();

        try
        {
            var (ok, msg) = await _wechat.HandleNotifyAsync(headers, body);
            //订单号在解密后的报文里，这里拿不到明文订单号
            return ok ? Ok(new { code = "SUCCESS", message = "成功" }) : BadRequest(new { code = "FAIL", message = msg });
        }
        catch (Exception ex)
        {
            //回调处理异常不能把细节外泄：回 4xx 让微信按策略重推，日志只记异常类型
            _logger.LogError(ex, "微信回调处理异常（{ExType}）", ex.GetType().Name);
            return BadRequest(new { code = "FAIL", message = "处理异常" });
        }
    }

    ///<summary>银行卡收银网关异步回调（正式通道）</summary>
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
            return Content(ok ? "success" : "failure", ContentTypeText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "银行卡回调处理异常（{ExType}）", ex.GetType().Name);
            return Content("failure", ContentTypeText);
        }
    }

    ///<summary>查询支付状态：前端支付页轮询</summary>
    [HttpGet("{orderNo}/status")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.PayStatus)]
    public async Task<IActionResult> GetPayStatus(string orderNo)
    {
        //── 会员购买（HY 交易号）
        if (orderNo.StartsWith("HY")) return await MemberPayStatus(orderNo);

        var order = await _svc.FindOrderAsync(orderNo);
        //补缴单（out_trade_no 为 SP- 前缀）没有租赁订单号
        if (order == null)
        {
            var supplementPayId = await _svc.FindSupplementPayIdAsync(orderNo);
            if (supplementPayId == null)
                return NotFound(new ApiResponse<object> { Code = 404, Message = "订单不存在（补缴单亦无此交易号）", Success = false });
            return await SupplementPayStatus(supplementPayId.Value);
        }

        if (IsCustomer && order.CustomerId != CurrentUserId) return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "无权查看该订单", Success = false });

        //若本地仍为待支付，向支付渠道主动查一次
        await RefreshPendingOrderAsync(order);

        //取本单最近一笔「钱到过账」的支付记录回传交易号：结算后退押金会把记录改成「部分退款」
        var record = await _svc.LatestArrivedAsync(order.OrderId);

        //「支付成功」不能只盯「已支付/租赁中」两个状态
        var paid = order.OrderStatus is "已支付" or "租赁中" or "已完结" || PayStatuses.IsMoneyArrived(record?.PayStatus);

        return Ok(new ApiResponse<PayStatusDto>
        {
            Data = new PayStatusDto
            {
                OrderNo = order.OrderNo,
                OrderId = order.OrderId,
                OrderStatus = order.OrderStatus,
                Paid = paid,
                Amount = IsAnonymous ? 0m : order.TotalRent + order.Deposit,
                TransactionNo = IsAnonymous ? "" : (record?.TransactionNo ?? "")
            }
        });
    }

    ///<summary>本地仍为待支付时按所选渠道查单：应对回调延迟/丢包，查完重载实体</summary>
    private async Task RefreshPendingOrderAsync(RentalOrder order)
    {
        //模拟收银台的入账由 mock/notify 主动推送，本地状态即真相，无需查单
        if (order.OrderStatus != "待支付") return;

        var latestType = await _svc.LatestPayTypeAsync(order.OrderId);

        //占位渠道（「待定」/空）要跳过：那表示顾客还没点过「去支付」，渠道侧压根没有这笔交易
        if (latestType.Length == 0 || latestType == PayStatuses.ChannelPlaceholder) return;

        //三个通道各查各的：银行卡订单必须走银行卡查单
        if (latestType == "微信") await _wechat.QueryTradeStatusAsync(order.OrderNo);
        else if (latestType == "银行卡") await _bank.QueryTradeStatusAsync(order.OrderNo);
        else await _alipay.QueryTradeStatusAsync(order.OrderNo);

        //查单兜底会当场幂等入账，必须重载
        await _svc.ReloadAsync(order);
    }

    ///<summary>补缴单状态查询（SP- 交易号）：口径与订单一致，待支付时按所选通道查单兜底</summary>
    private async Task<IActionResult> SupplementPayStatus(int payId)
    {
        var rec = await _svc.FindPayRecordAsync(payId, "supplement");
        if (rec == null) return NotFound(new ApiResponse<object> { Code = 404, Message = "补缴单不存在", Success = false });

        if (IsCustomer)
        {
            var own = await _svc.IsOrderOwnedByAsync(rec.OrderId, CurrentUserId);
            if (!own) return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "无权查看该补缴单", Success = false });
        }

        //本地仍待支付时向渠道查一次
        if (rec.PayStatus == "待支付" && rec.OutTradeNo is { Length: > 0 } tradeNo)
        {
            if (rec.PayType == "微信") await _wechat.QueryTradeStatusAsync(tradeNo);
            else if (rec.PayType == "银行卡") await _bank.QueryTradeStatusAsync(tradeNo);
            else if (rec.PayType == "支付宝") await _alipay.QueryTradeStatusAsync(tradeNo);

            rec = await _svc.FindPayRecordAsync(payId) ?? rec;
        }

        var paid = PayStatuses.IsMoneyArrived(rec.PayStatus);

        return Ok(new ApiResponse<PayStatusDto>
        {
            Data = new PayStatusDto
            {
                OrderNo = rec.OutTradeNo ?? "",
                OrderId = rec.OrderId,
                OrderStatus = paid ? "已支付" : rec.PayStatus,
                Paid = paid,
                Amount = IsAnonymous ? 0m : rec.PayAmount,
                TransactionNo = IsAnonymous ? "" : (rec.TransactionNo ?? ""),
                BizType = "supplement",
                PayId = rec.PayId
            }
        });
    }

    ///<summary>会员购买状态查询（HY 交易号）：读取 memberpay</summary>
    private async Task<IActionResult> MemberPayStatus(string tradeNo)
    {
        var view = await _member.GetPurchaseView(tradeNo);
        if (!view.Exists) return NotFound(new ApiResponse<object> { Code = 404, Message = "会员购买记录不存在", Success = false });

        var paid = view.Done;
        var statusText = view.Done ? "已支付" : "待支付";
        var alipayNo = view.AlipayTradeNo;

        //时间戳由 MemberService 用 DateTime.Now 写入（本地时间、Kind=Unspecified）
        if (!paid && DateTime.Now - view.CreateTime > TimeSpan.FromHours(MemberService.PendingExpireHours))   //NOSONAR 两端均为本地时间，改用 UtcNow 会改变过期判定结果
            statusText = "已过期";

        if (!paid && statusText == "待支付")
            (paid, statusText, alipayNo) = await QueryMemberPurchaseAsync(tradeNo, statusText, alipayNo);

        return Ok(new ApiResponse<PayStatusDto>
        {
            Data = new PayStatusDto
            {
                OrderNo = tradeNo,
                OrderStatus = statusText,
                Paid = paid,
                Amount = IsAnonymous ? 0m : view.Amount,
                TransactionNo = IsAnonymous ? "" : alipayNo,
                BizType = "member"
            }
        });
    }

    ///<summary>会员购买查单兜底</summary>
    private async Task<(bool Paid, string StatusText, string AlipayNo)> QueryMemberPurchaseAsync(string tradeNo, string statusText, string alipayNo)
    {
        try
        {
            var (_, tradeStatus, tNo, totalAmount) = await _alipay.QueryTradeRawAsync(tradeNo);
            if (tradeStatus is not ("TRADE_SUCCESS" or "TRADE_FINISHED")) return (false, statusText, alipayNo);

            var paidAmt = decimal.TryParse(totalAmount, out var pa) ? pa : 0m;
            var (ok, msg, _, no) = await _member.ApplyPaidPurchaseAsync(tradeNo, tNo, "支付宝", paidAmt);
            _logger.LogInformation("会员购买 {TradeNo} 查单兜底入账：{Result}（{Message}）", tradeNo, ok ? "成功" : "拒绝", msg);
            if (ok) return (true, "已支付", no);
            return (false, msg.Contains("过期") ? "已过期" : statusText, alipayNo);
        }
        catch (Exception ex)
        {
            //查单失败不打断轮询，回退暂存状态
            _logger.LogWarning(ex, "查询会员购买 {TradeNo} 状态失败（{ExType}）", tradeNo, ex.GetType().Name);
            return (false, statusText, alipayNo);
        }
    }

    [HttpPut("{id:int}/refund")]
    [Authorize(Policy = Policies.ReceptionOrFinance)]
    public async Task<IActionResult> RefundPayment(int id, [FromBody] decimal amount)
    {
        try
        {
            //返回值区分「真的原路退回」与「仅账面登记」，前台/财务必须看得见差别
            var r = await _svc.Refund(id, amount);
            return Ok(new ApiResponse<object> { Message = r.Message, Data = r });
        }
        catch (Exception ex) when (ErrorText.IsHandled(ex)) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }
}

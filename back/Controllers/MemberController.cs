using System.Security.Claims;
using ERental.DTOs;
using ERental.Models;
using ERental.Services;
using ERental.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERental.Controllers;

[ApiController]
[Route("api/[controller]")]
// 授权拆到方法级：顾客端会员中心（顾客）与管理员侧会员管理（管理员）共用一个控制器，
// 类级 Roles 会与方法级 Roles 取交集，导致两边都进不来，所以这里不能挂类级特性。
public class MemberController(MemberService svc, AlipayService alipay, PayConfigService payConfig) : ControllerBase
{
    private readonly MemberService _svc = svc;
    private readonly AlipayService _alipay = alipay;
    private readonly PayConfigService _payConfig = payConfig;

    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    /// <summary>浏览器此刻的访问源：优先前端拦截器注入的 X-Client-Origin，缺失时退回 Referer</summary>
    private string? ClientOrigin =>
        PayConfigService.ResolveClientOrigin(Request.Headers["X-Client-Origin"].ToString(), Request.Headers.Referer.ToString());

    /// <summary>会员中心：当前等级 + 可购买项 + 本人购买记录（顾客本人）</summary>
    [HttpGet("center")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> GetMemberCenter()
    {
        try { return Ok(new ApiResponse<MemberCenterDto> { Data = await _svc.GetCenter(CurrentUserId) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>发起会员购买支付：与租车订单同一套支付链路（支付宝/微信/银行卡）</summary>
    [HttpPost("pay/create")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> CreateMemberPay([FromBody] MemberBuyRequest dto)
    {
        try
        {
            var (tradeNo, price) = await _svc.CreatePurchase(CurrentUserId, dto.MemberLevel, dto.PayType);
            var payType = string.IsNullOrWhiteSpace(dto.PayType) ? "支付宝" : dto.PayType.Trim();
            var rt = await _payConfig.ResolveAsync();
            var realAlipay = rt is { IsReal: true, CredentialsComplete: true };

            string payUrl;
            if (payType == "支付宝")
            {
                // 回跳地址按顾客此刻的访问源生成（与订单/补缴同一口径）：
                // 不传就只能用配置里的 pay.return_url，换 IP 或局域网访问时会被甩到另一个地址。
                var returnUrl = await _payConfig.BuildReturnUrlAsync(ClientOrigin, "pay-result");
                var notifyUrl = await _payConfig.BuildNotifyUrlAsync(ClientOrigin, "api/payment/alipay/notify");
                // CreatePagePayAsync 内部自带三级降级：凭证不全时自动返回模拟收银台地址
                payUrl = await _alipay.CreatePagePayAsync(tradeNo, $"会员购买 {dto.MemberLevel}", price, returnUrl, notifyUrl);
            }
            else
            {
                // 微信 / 银行卡：课设环境无真实通道，与租车订单一致走内置模拟收银台
                payUrl = $"/mock-pay?orderNo={Uri.EscapeDataString(tradeNo)}&amount={price:0.00}" +
                         $"&payType={Uri.EscapeDataString(payType)}&subject={Uri.EscapeDataString("会员购买 " + dto.MemberLevel)}";
            }

            return Ok(new ApiResponse<CreatePayResultDto>
            {
                Data = new CreatePayResultDto
                {
                    OrderNo = tradeNo,
                    PayType = payType,
                    Amount = price,
                    PayUrl = payUrl,
                    RealPay = payType == "支付宝" && realAlipay
                }
            });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>会员类型列表（管理员/前台）：member_rights 全量 + 每类型客户数</summary>
    [HttpGet("rights")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> QueryMemberRights()
    {
        try { return Ok(new ApiResponse<List<MemberRightsDto>> { Data = await _svc.GetRights() }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>新增会员类型（管理员）</summary>
    [HttpPost("rights")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> AddMemberRight([FromBody] MemberRightSaveRequest dto)
    {
        try
        {
            var r = await _svc.CreateRight(dto, CurrentUserId);
            return Ok(new ApiResponse<MemberRightsDto> { Data = r, Message = $"会员类型「{r.MemberLevel}」已创建" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>编辑会员类型（管理员）：3NF——折扣/购买价即改即生效，客户档案不存折扣、无需同步</summary>
    [HttpPut("rights/{id:int}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> UpdateMemberRight(int id, [FromBody] MemberRightSaveRequest dto)
    {
        try
        {
            await _svc.UpdateRight(id, dto, CurrentUserId);
            return Ok(new ApiResponse<object>
            {
                Message = "会员类型已更新，全体挂靠客户即刻按新折扣计价"
            });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>某客户的会员购买 / 调整记录（工作人员查看，来自操作日志</summary>
    [HttpGet("history/{customerId:int}")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> GetMemberHistory(int customerId)
    {
        try { return Ok(new ApiResponse<List<MemberPurchaseDto>> { Data = await _svc.GetMemberHistory(customerId) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }
}

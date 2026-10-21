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
public class MemberController(
    MemberService svc, AlipayService alipay, PayConfigService payConfig) : ControllerBase
{
    private readonly MemberService _svc = svc;
    private readonly AlipayService _alipay = alipay;
    private readonly PayConfigService _payConfig = payConfig;

    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    /// <summary>会员中心：当前等级 + 可购买项 + 本人购买记录（顾客本人）</summary>
    [HttpGet("center")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> Center()
    {
        try { return Ok(new ApiResponse<MemberCenterDto> { Data = await _svc.GetCenter(CurrentUserId) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>
    /// 发起会员购买支付：与租车订单同一套支付链路（支付宝/微信/银行卡）。
    /// 生成 HY 开头交易号并暂存购买意向，返回收银台地址——
    /// 支付宝配置齐全时跳官方收银台，否则（含微信/银行卡）走内置模拟收银台。
    /// 真正的升级发生在支付入账时（回调/查单兜底，见 AlipayService.ApplyTradeResultAsync 的 HY 分支）。
    /// </summary>
    [HttpPost("pay/create")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> CreatePay([FromBody] MemberBuyRequest dto)
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
                // CreatePagePayAsync 内部自带三级降级：凭证不全时自动返回模拟收银台地址
                payUrl = await _alipay.CreatePagePayAsync(tradeNo, $"会员购买 {dto.MemberLevel}", price);
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

    /// <summary>
    /// 会员类型列表（管理员/前台）：member_rights 全量 + 每类型客户数。
    /// 「会员管理」页只维护类型本身；顾客的等级调整在「客户管理」页进行——
    /// 客户管理页对前台开放，其等级下拉依赖本接口做只读查找，因此这里必须放行前台
    /// （类型的新增/编辑仍仅管理员，见下方 POST/PUT）。
    /// </summary>
    [HttpGet("rights")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> Rights()
    {
        try { return Ok(new ApiResponse<List<MemberRightsDto>> { Data = await _svc.GetRights() }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>新增会员类型（管理员）</summary>
    [HttpPost("rights")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> CreateRight([FromBody] MemberRightSaveRequest dto)
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
    public async Task<IActionResult> UpdateRight(int id, [FromBody] MemberRightSaveRequest dto)
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

    /// <summary>某客户的会员购买 / 调整记录（工作人员查看，来自操作日志；已注销客户同样可查）</summary>
    [HttpGet("history/{customerId:int}")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> History(int customerId)
    {
        try { return Ok(new ApiResponse<List<MemberPurchaseDto>> { Data = await _svc.GetMemberHistory(customerId) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }
}

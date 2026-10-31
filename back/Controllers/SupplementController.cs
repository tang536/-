using System.Security.Claims;
using ERental.Authorization;
using ERental.DTOs;
using ERental.Models;
using ERental.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERental.Controllers;

/// <summary>补缴（逾期/损坏费用）统一入口：顾客自助端 + 门店收银端合并到同一控制器， 按角色策略分发（与消息中心 Messa…</summary>
[ApiController]
[Route("api/[controller]")]
public class SupplementController(SupplementService svc) : ControllerBase
{
    private readonly SupplementService _svc = svc;

    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    // ───────── 顾客自助端 ─────────

    /// <summary>顾客查询自己的补缴单列表（含待支付/已支付），用于「我的订单 → 待补缴」展示</summary>
    [HttpGet("mine")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> GetMySupplementBills()
    {
        try { return Ok(new ApiResponse<object> { Data = await _svc.GetMineAsync(CurrentUserId) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>为某张待支付补缴单发起支付，复用支付宝/微信/银行卡三通道，返回收银台地址或二维码</summary>
    [HttpPost("{payId:int}/pay")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> PaySupplement(int payId, [FromBody] string? payTypeRaw)
    {
        try
        {
            var data = await _svc.PayAsync(payId, CurrentUserId, payTypeRaw);
            return Ok(new ApiResponse<CreatePayResultDto> { Data = data });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>查询某张补缴单支付状态（含通道查单兜底），Paid=true 表示已入账</summary>
    [HttpGet("{payId:int}/status")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> GetSupplementPayStatus(int payId)
    {
        try { return Ok(new ApiResponse<object> { Data = await _svc.GetStatusAsync(payId, CurrentUserId) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    // ───────── 门店收银端 ─────────

    /// <summary>待收款补缴账单（前台入口）</summary>
    [HttpGet("bills")]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public async Task<IActionResult> ListSupplementBills([FromQuery] bool outstanding = true)
    {
        try { return Ok(new ApiResponse<object> { Data = await _svc.GetBillsAsync(outstanding) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>补缴收款：现金当场确认落账</summary>
    [HttpPost("settle/{settleId:int}/collect")]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public async Task<IActionResult> CollectSupplement(int settleId, [FromBody] SupplementCollectDto dto)
    {
        try
        {
            var r = await _svc.CollectAsync(settleId, dto.Channel);
            return Ok(new ApiResponse<SupplementCollectResultDto> { Data = r, Message = r.Message });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>查询某张结算单的补缴收款状态（前台轮询</summary>
    [HttpGet("settle/{settleId:int}/status")]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public async Task<IActionResult> GetCollectStatus(int settleId)
    {
        try { return Ok(new ApiResponse<SupplementBillDto> { Data = await _svc.GetStatusBySettleAsync(settleId) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>催缴：提醒顾客还有补缴款未缴清</summary>
    [HttpPost("settle/{settleId:int}/remind")]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public async Task<IActionResult> RemindSupplement(int settleId)
    {
        try
        {
            var amount = await _svc.RemindAsync(settleId);
            return Ok(new ApiResponse<object> { Message = $"已提醒顾客补缴 ¥{amount:0.00}，顾客端消息中心会立刻出现未读提醒" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }
}

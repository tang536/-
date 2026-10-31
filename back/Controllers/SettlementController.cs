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
// 归还车辆结算：用况3 的参与者为「前台（发起者）、客户」。
// 办理权（试算、落库结算）仅前台——管理员不在此放行，资金视角走用况5 统计报表；
// 但**结果查看与补缴付款**属顾客本人，故不整类挂策略，改为逐动作标注，
// 否则顾客连自己的结算单都读不到。
[Authorize(Policy = Policies.AnyAuthenticated)]
public class SettlementController(SettlementService svc) : ControllerBase
{
    private readonly SettlementService _svc = svc;

    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    // ───────── 顾客侧：查看自己的结算单并选择付款方式 ─────────

    /// <summary>我的结算单：前台审核还车后生成的账目凭据（租金构成 / 逾期费 / 损坏费 / 应退应补）， 顾客据此决定「在线支付…</summary>
    [HttpGet("mine")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> GetMySettlements()
    {
        try { return Ok(new ApiResponse<List<MySettleDto>> { Data = await _svc.GetMineAsync(CurrentUserId) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>顾客为结算单的补缴款发起在线支付（支付宝/微信/银行卡三选一），返回收银台地址或二维码</summary>
    [HttpPost("{settleId:int}/pay-online")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> PaySettlementOnline(int settleId, [FromBody] string? channel)
    {
        try { return Ok(new ApiResponse<CreatePayResultDto> { Data = await _svc.PayOnlineAsync(settleId, CurrentUserId, channel) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    // ───────── 前台侧：结算办理 ─────────

    [HttpGet]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public async Task<IActionResult> QuerySettleRecords() => Ok(new ApiResponse<object> { Data = await _svc.GetAll() });

    /// <summary>结算试算：前台在弹窗里选定订单后，立刻显示系统将自动计收的逾期费 （未逾期也返回 0，前端照样把这一行显示出来），并…</summary>
    [HttpGet("preview/{orderId:int}")]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public async Task<IActionResult> CalcSettleAmount(int orderId)
    {
        try { return Ok(new ApiResponse<SettlePreviewDto> { Data = await _svc.Preview(orderId) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    [HttpPost("settle")]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public async Task<IActionResult> ConfirmSettle([FromBody] SettleOrderDto dto)
    {
        try
        {
            var opId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var result = await _svc.Settle(dto, opId);
            // 结算一完成就要判断"要不要立刻弹收款窗"，把这个判断放在后端给出，
            // 前端不必拿 supplementAmount 再猜一次（也避免两处口径漂移）。
            return Ok(new ApiResponse<object>
            {
                Data = new
                {
                    result.Record.SettleId,
                    result.Record.SupplementAmount,
                    result.Record.RefundAmount,
                    NeedCollect = result.Record.SupplementAmount > 0,
                    result.Record
                },
                Message = result.Message
            });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }
}

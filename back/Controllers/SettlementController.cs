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
// 归还车辆结算：用况3 的参与者为「前台（发起者）、客户」，因此整组接口只授权前台。
// 财务的资金视角（营收 / 退款 / 对账）走用况5 统计报表，不进入结算操作页。
[Authorize(Policy = Policies.ReceptionOnly)]
public class SettlementController(SettlementService svc) : ControllerBase
{
    private readonly SettlementService _svc = svc;

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(new ApiResponse<object> { Data = await _svc.GetAll() });

    /// <summary>
    /// 结算试算：前台在弹窗里选定订单后，立刻显示系统将自动计收的逾期费
    /// （**未逾期也返回 0**，前端照样把这一行显示出来），并给出实际租金与预估退还/补缴。
    /// 与结算接口共用同一段核算，试算金额即为落库金额。
    /// </summary>
    [HttpGet("preview/{orderId:int}")]
    public async Task<IActionResult> Preview(int orderId)
    {
        try { return Ok(new ApiResponse<SettlePreviewDto> { Data = await _svc.Preview(orderId) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    [HttpPost("settle")]
    public async Task<IActionResult> Settle([FromBody] SettleOrderDto dto)
    {
        try
        {
            var opId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var result = await _svc.Settle(dto, opId);
            return Ok(new ApiResponse<object> { Data = result.Record, Message = result.Message });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }
}

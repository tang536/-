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
// 归还车辆结算：用况3 的参与者为「前台（发起者）、客户」，办理权仅前台。
// 管理员不在此放行——资金办理（结算、补缴收款、催缴）只认前台这一双手；
// 管理员/财务的资金视角走用况5 统计报表（AdminOrFinance / AnyStaff），两边互不越界。
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

    /// <summary>
    /// 待收款补缴账单（前台入口）。
    /// 只返回**未收清**的单子——这就是「入口当且仅当逾期/欠费未缴清才存在」的服务端保证：
    /// 前端不需要自己判断该不该显示，列表为空就不显示入口。
    /// </summary>
    [HttpGet("supplement/bills")]
    public async Task<IActionResult> SupplementBills([FromQuery] bool outstanding = true)
    {
        try { return Ok(new ApiResponse<object> { Data = await _svc.GetSupplementBillsAsync(outstanding) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>
    /// 补缴收款：现金当场确认落账；支付宝/微信/银行卡返回收银台地址或二维码。
    /// </summary>
    [HttpPost("{settleId:int}/supplement/collect")]
    public async Task<IActionResult> CollectSupplement(int settleId, [FromBody] SupplementCollectDto dto)
    {
        try
        {
            var r = await _svc.CollectSupplementAsync(settleId, dto.Channel);
            return Ok(new ApiResponse<SupplementCollectResultDto> { Data = r, Message = r.Message });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>查询某张结算单的补缴收款状态（前台轮询；待支付时按通道查单兜底）。</summary>
    [HttpGet("{settleId:int}/supplement/status")]
    public async Task<IActionResult> SupplementStatus(int settleId)
    {
        try { return Ok(new ApiResponse<SupplementBillDto> { Data = await _svc.GetSupplementStatusAsync(settleId) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>
    /// 催缴：顾客关了支付页就找不到人，前台需要有「再提醒一次」的入口。
    /// 只针对该结算单产生且仍为「待支付」的线上补缴单，重复点不会刷出多条通知。
    /// </summary>
    [HttpPost("{settleId:int}/remind-supplement")]
    public async Task<IActionResult> RemindSupplement(int settleId)
    {
        try
        {
            var amount = await _svc.RemindSupplementAsync(settleId);
            return Ok(new ApiResponse<object> { Message = $"已提醒顾客补缴 ¥{amount:0.00}，顾客端消息中心会立刻出现未读提醒" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }
}

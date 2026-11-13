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
//归还车辆结算
[Authorize(Policy = Policies.AnyAuthenticated)]
public class SettlementController(SettlementService svc) : ControllerBase
{
    private readonly SettlementService _svc = svc;

    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    //顾客侧：查看自己的结算单并选择付款方式

    ///<summary>我的结算单：前台审核还车后生成的账目凭据（租金构成/逾期费/损坏费/应退应补）</summary>
    [HttpGet("mine")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> GetMySettlements()
    {
        try { return Ok(new ApiResponse<List<MySettleDto>> { Data = await _svc.GetMineAsync(CurrentUserId) }); }
        catch (Exception ex) when (ErrorText.IsHandled(ex)) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    ///<summary>顾客为结算单的补缴款发起在线支付（支付宝/微信/银行卡三选一），返回收银台地址或二维码</summary>
    [HttpPost("{settleId:int}/pay-online")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> PaySettlementOnline(int settleId, [FromBody] string? channel)
    {
        try { return Ok(new ApiResponse<CreatePayResultDto> { Data = await _svc.PayOnlineAsync(settleId, CurrentUserId, channel) }); }
        catch (Exception ex) when (ErrorText.IsHandled(ex)) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    //前台侧：结算办理

    ///<summary>
    /// 结算记录查询（分页）：管理员、前台、财务都能看。
    /// 管理员核账、财务对账退款都要看这张表，所以放行 AnyStaff；但**只是看**——
    /// 结算发起、确认收款、催缴都在下面的写接口（ReceptionOnly），财务与管理员手里的按钮
    /// 由前端 canOperate 统一收掉，后端写接口也不认这两类角色。
    ///</summary>
    [HttpGet]
    [Authorize(Policy = Policies.SettleRead)]
    public async Task<IActionResult> QuerySettleRecords(DateTime? from, DateTime? to, string? keyword, int page = 1, int pageSize = 20)
        => Ok(new ApiResponse<object> { Data = await _svc.GetPaged(from, to, page, pageSize, keyword) });

    //交接验车：取车 / 还车各留一条证据（电量 + 照片 + 车况备注），损坏费的定责依据

    ///<summary>某订单的交接验车记录；三类员工都能看（管理员核账、财务核赔、前台补登记）</summary>
    [HttpGet("handover/{orderId:int}")]
    [Authorize(Policy = Policies.SettleRead)]
    public async Task<IActionResult> GetHandovers(int orderId)
        => Ok(new ApiResponse<object> { Data = await _svc.ListHandovers(orderId) });

    ///<summary>登记 / 覆盖交接验车（柜台动作，仍只归前台）</summary>
    [HttpPost("handover")]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public async Task<IActionResult> SaveHandover([FromBody] SaveHandoverDto dto)
    {
        try { return Ok(new ApiResponse<object> { Data = await _svc.SaveHandover(dto, CurrentUserId), Message = "验车记录已保存" }); }
        catch (Exception ex) when (ErrorText.IsHandled(ex)) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    ///<summary>上传验车照片（multipart/form-data，字段名 file；orderId/phase 用于命名与分目录）</summary>
    [HttpPost("handover/upload")]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public async Task<IActionResult> UploadHandoverPhoto(IFormFile? file, int orderId = 0, string? phase = null)
    {
        try { return Ok(new ApiResponse<object> { Data = new { url = await _svc.SaveHandoverPhoto(file, orderId, phase) }, Message = "照片上传成功" }); }
        catch (Exception ex) when (ErrorText.IsHandled(ex)) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    ///<summary>结算试算：前台在弹窗里选定订单后，立刻显示系统将自动计收的逾期费</summary>
    [HttpGet("preview/{orderId:int}")]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public async Task<IActionResult> CalcSettleAmount(int orderId)
    {
        try { return Ok(new ApiResponse<SettlePreviewDto> { Data = await _svc.Preview(orderId) }); }
        catch (Exception ex) when (ErrorText.IsHandled(ex)) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    [HttpPost("settle")]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public async Task<IActionResult> ConfirmSettle([FromBody] SettleOrderDto dto)
    {
        try
        {
            var opId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var result = await _svc.Settle(dto, opId);
            //结算一完成就要判断"要不要立刻弹收款窗"，NeedCollect 由服务层算好，控制器不再拼装金额
            return Ok(new ApiResponse<SettleResultDto> { Data = result.Record, Message = result.Message });
        }
        catch (Exception ex) when (ErrorText.IsHandled(ex)) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }
}

using System.Security.Claims;
using ERental.DTOs;
using ERental.Services;
using ERental.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ApiResponse = ERental.Models.ApiResponse<object>;
using CreateReq = ERental.DTOs.CreateMaintenanceRequest;
using CompleteReq = ERental.DTOs.CompleteMaintenanceRequest;
using UpdateReq = ERental.DTOs.UpdateMaintenanceRequest;

namespace ERental.Controllers;

[ApiController]
[Route("api/[controller]")]
// 故障上报（新增工单）对前台开放 —— 需求 4.2.6「前台、管理人员发现车辆故障后可上报」；
// 工单完结、报废记录查询与报废操作属「用况4 车辆全生命周期管理」，仅管理员。
[Authorize(Policy = Policies.AdminOrReception)]
public class MaintenanceController(MaintenanceService svc) : ControllerBase
{
    private readonly MaintenanceService _svc = svc;

    /// <summary>维保工单列表（管理员 / 前台）</summary>
    [HttpGet]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> GetAll(string? status) => Ok(new ApiResponse { Data = await _svc.GetAll(status) });

    /// <summary>创建维保工单：入参走 DTO，工单号由服务端生成</summary>
    [HttpPost]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> Create([FromBody] CreateReq req)
    {
        try
        {
            var dto = await _svc.Create(req, CurrentOperatorId());
            return Ok(new ApiResponse { Data = dto, Message = "工单创建成功" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>
    /// 上传故障现场照片（multipart/form-data，字段名 file）。
    /// 返回 { url }，前端把它放进新建工单的 faultImage 字段即可随工单锁定存档。
    /// 管理员与前台都可上传（前台上报故障时需要拍照定位）。
    /// </summary>
    [HttpPost("upload")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> UploadImage(IFormFile? file)
    {
        try
        {
            var url = await _svc.SaveImage(file);
            return Ok(new ApiResponse { Data = new { url }, Message = "照片上传成功" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>完成维保：入参为 { fee } 对象，避免裸数字绑定失败</summary>
    [HttpPut("{id:int}/complete")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Complete(int id, [FromBody] CompleteReq req)
    {
        try { await _svc.Complete(id, req.Fee); return Ok(new ApiResponse { Message = "工单已完成" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>编辑维保工单内容（故障类型 / 描述 / 现场照片），前台与管理员均可操作，已完 workpiece 工单不可编辑</summary>
    [HttpPut("{id:int}")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateReq req)
    {
        try { await _svc.Update(id, req); return Ok(new ApiResponse { Message = "工单已更新" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>撤回维保工单：撤销该工单并释放车辆（仅当无其它未完成工单时置空闲）。前台与管理员均可操作。</summary>
    [HttpPost("{id:int}/withdraw")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> Withdraw(int id)
    {
        try { await _svc.Withdraw(id); return Ok(new ApiResponse { Message = "工单已撤回，车辆已释放" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>报废记录（管理员 / 前台可查）</summary>
    [HttpGet("scraps")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> GetScraps() => Ok(new ApiResponse { Data = await _svc.GetScraps() });

    /// <summary>车辆报废：资产处置，仅管理员</summary>
    [HttpPost("scrap")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Scrap([FromBody] ScrapRequest req)
    {
        try
        {
            await _svc.ScrapVehicle(req.VehicleId, req.Reason, CurrentOperatorId() ?? 0);
            return Ok(new ApiResponse { Message = "车辆已报废" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    private int? CurrentOperatorId() => int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
}

using ERental.DTOs;
using ERental.Models;
using ERental.Services;
using ERental.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERental.Controllers;

[ApiController]
[Route("api/[controller]")]
// 车辆盘点属「用况4 车辆全生命周期管理」，参与者仅管理员
[Authorize(Policy = Policies.AdminOnly)]
public class CheckController(CheckService svc) : ControllerBase
{
    private readonly CheckService _svc = svc;

    [HttpGet("list")]
    public async Task<IActionResult> GetChecks(DateTime? date) =>
        Ok(new ApiResponse<List<CarCheckList>> { Data = await _svc.GetAll(date) });

    [HttpPost("add")]
    public async Task<IActionResult> AddCheck([FromBody] CheckRequest req)
    {
        try { return Ok(new ApiResponse<CarCheckList> { Data = await _svc.AddCheck(req.VehicleId, req.ActualStatus, req.DiffDesc) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    [HttpGet("reports")]
    public async Task<IActionResult> GetReports() =>
        Ok(new ApiResponse<List<CheckReport>> { Data = await _svc.GetReports() });

    [HttpPost("generate-report")]
    public async Task<IActionResult> GenerateReport([FromBody] GenReportRequest req) =>
        Ok(new ApiResponse<CheckReport> { Data = await _svc.GenerateReport(req.Date) });
}

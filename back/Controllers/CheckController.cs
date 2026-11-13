using ERental.DTOs;
using ERental.Models;
using ERental.Services;
using ERental.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERental.Controllers;

[ApiController]
[Route("api/[controller]")]
//车辆盘点属「车辆全生命周期管理」
[Authorize(Policy = Policies.AdminOnly)]
public class CheckController(CheckService svc) : ControllerBase
{
    private readonly CheckService _svc = svc;

    [HttpGet("list")]
    public async Task<IActionResult> QueryCheckRecords(DateTime? date) =>
        Ok(new ApiResponse<List<CarCheckListDto>> { Data = await _svc.GetAll(date) });

    [HttpPost("add")]
    public async Task<IActionResult> AddCarCheckRecord([FromBody] CheckRequest req)
    {
        try { return Ok(new ApiResponse<CarCheckListDto> { Data = await _svc.AddCheck(req.VehicleId, req.ActualStatus, req.DiffDesc) }); }
        catch (Exception ex) when (ErrorText.IsHandled(ex)) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    [HttpGet("reports")]
    public async Task<IActionResult> QueryCheckReports() =>
        Ok(new ApiResponse<List<CheckReportDto>> { Data = await _svc.GetReports() });

    [HttpPost("generate-report")]
    public async Task<IActionResult> GenerateCheckReport([FromBody] GenReportRequest req) =>
        Ok(new ApiResponse<CheckReportDto> { Data = await _svc.GenerateReport(req.Date) });
}

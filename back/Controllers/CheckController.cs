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
    public async Task<IActionResult> GetChecks(DateTime? date) => Ok(new ApiResponse<List<CarCheckListDto>>
        {
            Data = (await _svc.GetAll(date)).Select(CarCheckListDto.From).ToList()
        });

    [HttpPost("add")]
    public async Task<IActionResult> AddCheck([FromBody] CheckRequest req)
    {
        try
        {
            var row = await _svc.AddCheck(req.VehicleId, req.ActualStatus, req.DiffDesc);
            return Ok(new ApiResponse<CarCheckListDto> { Data = CarCheckListDto.From(row) });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    [HttpGet("reports")]
    public async Task<IActionResult> GetReports() => Ok(new ApiResponse<List<CheckReportDto>>
        {
            Data = (await _svc.GetReports()).Select(CheckReportDto.From).ToList()
        });

    [HttpPost("generate-report")]
    public async Task<IActionResult> GenerateReport([FromBody] GenReportRequest req) => Ok(new ApiResponse<CheckReportDto>
        {
            Data = CheckReportDto.From(await _svc.GenerateReport(req.Date))
        });
}

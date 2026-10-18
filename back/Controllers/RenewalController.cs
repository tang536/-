using ERental.DTOs;
using ERental.Models;
using ERental.Services;
using ERental.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERental.Controllers;

[ApiController]
[Route("api/[controller]")]
// 续租属「用况2 创建租赁订单」的延伸，由前台发起
[Authorize(Policy = Policies.AdminOrReception)]
public class RenewalController(RenewalService svc) : ControllerBase
{
    private readonly RenewalService _svc = svc;

    [HttpGet]
    public async Task<IActionResult> GetAll(int? orderId) =>
        Ok(new ApiResponse<List<RenewalRecord>> { Data = await _svc.GetAll(orderId) });

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] RenewalRequest req)
    {
        try { return Ok(new ApiResponse<RenewalRecord> { Data = await _svc.Create(req.OrderId, req.Hours) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }
}

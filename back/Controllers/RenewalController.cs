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
//续租属「创建租赁订单」的延伸，由前台发起
[Authorize(Policy = Policies.AdminOrReception)]
public class RenewalController(RenewalService svc) : ControllerBase
{
    private readonly RenewalService _svc = svc;
    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    [HttpGet]
    public async Task<IActionResult> QueryRenewalRecords(int? orderId) => Ok(new ApiResponse<List<RenewalDto>> { Data = await _svc.GetAll(orderId) });

    [HttpPost]
    public async Task<IActionResult> RenewOrder([FromBody] RenewalRequest req)
    {
        try { return Ok(new ApiResponse<RenewalDto> { Data = await _svc.Create(req.OrderId, req.Days, CurrentUserId) }); }
        catch (Exception ex) when (ErrorText.IsHandled(ex)) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }
}

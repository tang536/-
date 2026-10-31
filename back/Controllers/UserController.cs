using ERental.DTOs;
using ERental.Models;
using ERental.Services;
using ERental.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERental.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = Policies.AdminOnly)]
public class UserController(UserService svc) : ControllerBase
{
    private readonly UserService _svc = svc;

    [HttpGet]
    public async Task<IActionResult> QueryStaffAccounts() => Ok(new ApiResponse<List<UserDto>> { Data = await _svc.GetAllStaff() });

    [HttpPost]
    public async Task<IActionResult> AddStaffAccount([FromBody] CreateUserDto dto)
    {
        try { return Ok(new ApiResponse<UserDto> { Data = await _svc.CreateStaff(dto) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> EditStaffAccount(int id, [FromBody] UpdateUserDto dto)
    {
        try { await _svc.UpdateStaff(id, dto); return Ok(new ApiResponse<object> { Message = "更新成功" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>停用员工账号：置为停用并立即吊销该账号全部在线会话，旧令牌随即失效</summary>
    [HttpPut("{id:int}/disable")]
    public async Task<IActionResult> DisableStaffAccount(int id)
    {
        try
        {
            await _svc.UpdateStaff(id, new UpdateUserDto { UserStatus = 0 });
            return Ok(new ApiResponse<object> { Message = "账号已停用" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteStaffAccount(int id)
    {
        try { await _svc.DeleteStaff(id); return Ok(new ApiResponse<object> { Message = "删除成功" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }
}

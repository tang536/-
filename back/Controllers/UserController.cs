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
    public async Task<IActionResult> GetAll() => Ok(new ApiResponse<List<UserDto>> { Data = await _svc.GetAllStaff() });


    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserDto dto)
    {
        try { return Ok(new ApiResponse<UserDto> { Data = await _svc.CreateStaff(dto) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateUserDto dto)
    {
        try { await _svc.UpdateStaff(id, dto); return Ok(new ApiResponse<object> { Message = "更新成功" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        try { await _svc.DeleteStaff(id); return Ok(new ApiResponse<object> { Message = "删除成功" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }
}

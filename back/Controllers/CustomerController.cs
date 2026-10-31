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
[Authorize(Policy = Policies.AnyAuthenticated)]
public class CustomerController(CustomerService svc) : ControllerBase
{
    private readonly CustomerService _svc = svc;

    private bool IsCustomer => User.IsInRole(AuthService.RoleCustomer);
    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    /// <summary>客户列表：仅工作人员可查（顾客不可遍历他人档案）</summary>
    [HttpGet]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> GetCustomerInfo(string? keyword, string? level, string? accountStatus, int page = 0, int pageSize = 0)
    {
        if (pageSize > 0)
        {
            var (items, total) = await _svc.GetPaged(keyword, level, accountStatus, page, pageSize);
            var (p, size, _, _) = Paging.Normalize(page, pageSize);
            return Ok(new ApiResponse<PagedResult<CustomerDto>>
            {
                Data = new PagedResult<CustomerDto>(items.Select(CustomerDto.From).ToList(), total, p, size)
            });
        }
        return Ok(new ApiResponse<List<CustomerDto>>
        {
            Data = (await _svc.GetAll(keyword)).Select(CustomerDto.From).ToList()
        });
    }

    /// <summary>客户详情：工作人员可查任意客户，顾客仅能查自己</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetCustomerInfoById(int id)
    {
        if (IsCustomer && id != CurrentUserId)
            return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "无权查看其他客户的资料", Success = false });
        try
        {
            var customer = await _svc.GetById(id);
            return Ok(new ApiResponse<CustomerDto> { Data = CustomerDto.From(customer) });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    [HttpPost]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> AddCustomerInfo([FromBody] CustomerCreateDto dto)
    {
        try
        {
            var created = await _svc.Create(dto);
            return Ok(new ApiResponse<CustomerDto> { Data = CustomerDto.From(created) });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> EditCustomerInfo(int id, [FromBody] CustomerUpdateDto dto)
    {
        try { await _svc.Update(id, dto); return Ok(new ApiResponse<object> { Message = "更新成功" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> DeleteCustomerInfo(int id)
    {
        try { await _svc.Delete(id); return Ok(new ApiResponse<object> { Message = "删除成功" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>客户消费记录（需求 4</summary>
    [HttpGet("{id:int}/records")]
    public async Task<IActionResult> GetCustomerRecords(int id, DateTime? from, DateTime? to, string? status)
    {
        if (IsCustomer && id != CurrentUserId)
            return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "无权查看其他客户的消费记录", Success = false });
        try { return Ok(new ApiResponse<CustomerRecordsDto> { Data = await _svc.GetRecordsAsync(id, from, to, status) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }
}

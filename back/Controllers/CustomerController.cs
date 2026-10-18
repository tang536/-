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

    /// <summary>
    /// 客户列表：仅工作人员可查（顾客不可遍历他人档案）。
    /// 传 <c>pageSize&gt;0</c> 返回分页结构，不传返回全量数组（兼容既有调用）。
    /// </summary>
    [HttpGet]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> GetAll(string? keyword, string? level, string? accountStatus,
        int page = 0, int pageSize = 0)
    {
        if (pageSize > 0)
        {
            var (items, total) = await _svc.GetPaged(keyword, level, accountStatus, page, pageSize);
            var (p, size, _, _) = Paging.Normalize(page, pageSize);
            return Ok(new ApiResponse<PagedResult<Customer>>
            {
                Data = new PagedResult<Customer>(items, total, p, size)
            });
        }
        return Ok(new ApiResponse<List<Customer>> { Data = await _svc.GetAll(keyword) });
    }

    /// <summary>客户详情：工作人员可查任意客户，顾客仅能查自己</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        if (IsCustomer && id != CurrentUserId)
            return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "无权查看其他客户的资料", Success = false });
        try { return Ok(new ApiResponse<Customer> { Data = await _svc.GetById(id) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    [HttpPost]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> Create([FromBody] Customer c)
    {
        try { return Ok(new ApiResponse<Customer> { Data = await _svc.Create(c) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> Update(int id, [FromBody] Customer c)
    {
        try { await _svc.Update(id, c); return Ok(new ApiResponse<object> { Message = "更新成功" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Delete(int id)
    {
        try { await _svc.Delete(id); return Ok(new ApiResponse<object> { Message = "删除成功" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    /// <summary>
    /// 客户消费记录（需求 4.2.3）：全量租赁、消费与支付记录，支持按时间与订单状态筛选，
    /// 可逐单展开详情，实现客户消费行为全周期追溯。
    ///
    /// 权限口径与 GetById 一致：工作人员可查任意客户，顾客仅能查自己。
    /// </summary>
    /// <param name="id">客户 ID</param>
    /// <param name="from">起始日（含）；不传表示不限制起点</param>
    /// <param name="to">截止日（含当天）</param>
    /// <param name="status">订单状态：待支付 / 已支付 / 租赁中 / 已完结 / 已取消</param>
    [HttpGet("{id:int}/records")]
    public async Task<IActionResult> GetRecords(int id, DateTime? from, DateTime? to, string? status)
    {
        if (IsCustomer && id != CurrentUserId)
            return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "无权查看其他客户的消费记录", Success = false });
        try { return Ok(new ApiResponse<CustomerRecordsDto> { Data = await _svc.GetRecordsAsync(id, from, to, status) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }
}

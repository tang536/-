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
// 订单查询：工作人员查全店，顾客只能读自己的（Service 内按 Token 中的 ID 过滤）。
// 开单 / 取消属用况2，发起者为前台。
[Authorize(Policy = Policies.AdminOrReceptionOrCustomer)]
public class OrderController(OrderService svc) : ControllerBase
{
    private readonly OrderService _svc = svc;

    private bool IsCustomer => User.IsInRole(AuthService.RoleCustomer);
    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    /// <summary>
    /// 订单列表。顾客角色只能看到自己的订单（后端强隔离，不依赖前端传参）。
    /// 传 <c>pageSize&gt;0</c> 返回分页结构，不传返回全量数组（顾客端"我的订单"等调用不受影响）。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(string? status, string? keyword, int page = 0, int pageSize = 0)
    {
        var customerId = IsCustomer ? CurrentUserId : (int?)null;
        if (pageSize > 0)
        {
            var (rows, total) = await _svc.GetPaged(status, keyword, customerId, page, pageSize);
            var (p, size, _, _) = Paging.Normalize(page, pageSize);
            return Ok(new ApiResponse<PagedResult<OrderDto>> { Data = new PagedResult<OrderDto>(rows, total, p, size) });
        }
        return Ok(new ApiResponse<List<OrderDto>> { Data = await _svc.GetAll(status, keyword, customerId) });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        try
        {
            var order = await _svc.GetById(id);
            if (IsCustomer && order.CustomerId != CurrentUserId)
                return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "无权查看该订单", Success = false });
            return Ok(new ApiResponse<object> { Data = order });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    /// <summary>线下开单。客户信息系统中不存在时，服务端自动建档再生成订单</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public async Task<IActionResult> Create([FromBody] CreateOrderDto dto)
    {
        try
        {
            var order = await _svc.Create(dto, CurrentUserId);
            return Ok(new ApiResponse<object> { Data = OrderResult(order), Message = "订单创建成功" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    /// <summary>线上下单（仅顾客）：必须先完成实名认证；客户身份取自 Token，忽略前端传入的 ID</summary>
    [HttpPost("online")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> CreateOnline([FromBody] OnlineOrderDto dto)
    {
        try
        {
            var order = await _svc.CreateOnline(CurrentUserId, dto);
            return Ok(new ApiResponse<object> { Data = OrderResult(order), Message = "线上下单成功，可打印小票" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    /// <summary>线上可租车辆（仅顾客）：只返回空闲车辆的窄字段，不暴露经营数据</summary>
    [HttpGet("available-vehicles")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> AvailableVehicles() =>
        Ok(new ApiResponse<List<RentableVehicleDto>> { Data = await _svc.GetRentableVehicles() });

    /// <summary>线上试算：按所选车辆取价，折扣取本人会员等级。vehicleId 可选，未选车时按系统默认价预估</summary>
    [HttpGet("estimate")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> Estimate(DateTime leaseStart, DateTime leaseEnd, int? vehicleId)
    {
        try { return Ok(new ApiResponse<RentEstimateDto> { Data = await _svc.Estimate(CurrentUserId, leaseStart, leaseEnd, vehicleId) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    /// <summary>
    /// 线下开单试算：前台为指定客户按其会员等级取折扣。
    /// 必须与服务端下单共用同一套口径（此前前端硬编码 days×30×discount、deposit=500，
    /// 与按小时、按车辆定价的真实计费不符，默认配置恰好是 30/500 才没暴露）。
    /// </summary>
    [HttpGet("staff-estimate")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> StaffEstimate(int customerId, DateTime leaseStart, DateTime leaseEnd, int? vehicleId)
    {
        try { return Ok(new ApiResponse<RentEstimateDto> { Data = await _svc.Estimate(customerId, leaseStart, leaseEnd, vehicleId) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    [HttpPut("{id:int}/cancel")]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public async Task<IActionResult> Cancel(int id, [FromBody] string reason)
    {
        try { await _svc.Cancel(id, reason); return Ok(new ApiResponse<object> { Message = "订单已取消" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    /// <summary>
    /// 顾客自助取消自己的「待支付」订单，立即释放车辆。
    /// 为什么要单独开这个接口：顾客点开收银台后反悔/支付失败，若不能自己取消，
    /// 就只能干等超时自动释放，车辆在这段时间被无意义占用。已支付的订单不允许自助取消。
    /// </summary>
    [HttpPost("{id:int}/cancel-pending")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> CancelPending(int id)
    {
        try
        {
            await _svc.CancelByCustomer(id, CurrentUserId);
            return Ok(new ApiResponse<object> { Message = "订单已取消，车辆已释放" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    /// <summary>顾客申请还车（无论是否到期）。审核留给前台：顾客自己点就把钱退了，门店对车况与费用会失去控制</summary>
    [HttpPost("{id:int}/early-return")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> RequestEarlyReturn(int id)
    {
        try
        {
            await _svc.RequestEarlyReturn(id, CurrentUserId);
            return Ok(new ApiResponse<object> { Message = "还车申请已提交，请等待门店审核" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    /// <summary>前台审核还车申请：approve=true 通过（随后走归还结算，超时费按倍数自动计），false 驳回。</summary>
    [HttpPost("{id:int}/early-return/review")]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public async Task<IActionResult> ReviewEarlyReturn(int id, [FromBody] EarlyReturnReviewDto dto)
    {
        try
        {
            await _svc.ReviewEarlyReturn(id, dto.Approve, dto.RejectReason);
            return Ok(new ApiResponse<object>
            {
                Message = dto.Approve ? "已通过还车申请，请完成归还结算" : "已驳回该还车申请"
            });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    /// <summary>开单结果投影：只回传前端需要的字段，避免直接序列化 EF 实体形成导航环</summary>
    private static object OrderResult(RentalOrder o) => new
    {
        o.OrderId,
        o.OrderNo,
        o.LeaseStart,
        o.LeaseEnd,
        o.TotalRent,
        o.Deposit,
        o.OrderStatus,
        Payable = o.TotalRent + o.Deposit
    };

    /// <summary>订单到期预警：返回即将到期（默认3天内）和已逾期的在租订单，前台工作台置顶提醒</summary>
    [HttpGet("warnings")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> Warnings(int warnDays = 3) =>
        Ok(new ApiResponse<object> { Data = await _svc.GetWarnings(warnDays) });
}

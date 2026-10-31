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
public class OrderController(OrderService svc, SettlementService settlement) : ControllerBase
{
    private readonly OrderService _svc = svc;
    private readonly SettlementService _settlement = settlement;

    private bool IsCustomer => User.IsInRole(AuthService.RoleCustomer);
    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    /// <summary>订单列表</summary>
    [HttpGet]
    public async Task<IActionResult> QueryOrderInfo(string? status, string? keyword, DateTime? dateFrom = null, DateTime? dateTo = null, string? sortBy = null, bool sortDesc = true, int page = 0, int pageSize = 0)
    {
        var customerId = IsCustomer ? CurrentUserId : (int?)null;
        if (pageSize > 0)
        {
            var (rows, total) = await _svc.GetPaged(
                status, keyword, customerId, dateFrom, dateTo, sortBy, sortDesc, page, pageSize);
            var (p, size, _, _) = Paging.Normalize(page, pageSize);
            return Ok(new ApiResponse<PagedResult<OrderDto>> { Data = new PagedResult<OrderDto>(rows, total, p, size) });
        }
        return Ok(new ApiResponse<List<OrderDto>>
        {
            Data = await _svc.GetAll(status, keyword, customerId, dateFrom, dateTo, sortBy, sortDesc),
        });
    }

    /// <summary>待还车订单（前台归还台）</summary>
    [HttpGet("wait-return")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public Task<IActionResult> GetWaitReturnOrder(string? keyword, DateTime? dateFrom = null, DateTime? dateTo = null, string? sortBy = null, bool sortDesc = true, int page = 0, int pageSize = 0)
        => QueryOrderInfo("已支付", keyword, dateFrom, dateTo, sortBy, sortDesc, page, pageSize);

    [HttpGet("{id:int}")]
    public async Task<IActionResult> QueryOrderInfoById(int id)
    {
        try
        {
            var order = await _svc.GetById(id);
            if (IsCustomer && order.CustomerId != CurrentUserId)
                return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "无权查看该订单", Success = false });
            return Ok(new ApiResponse<object> { Data = order });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>线下开单</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public async Task<IActionResult> ConfirmOrderAndPay([FromBody] CreateOrderDto dto)
    {
        try
        {
            var order = await _svc.Create(dto, CurrentUserId);
            return Ok(new ApiResponse<object> { Data = OrderResult(order), Message = "订单创建成功" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>柜台开单并当场收款</summary>
    [HttpPost("staff")]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public Task<IActionResult> CreateStaffOrder([FromBody] CreateOrderDto dto) => ConfirmOrderAndPay(dto);

    /// <summary>线上下单（仅顾客）：必须先完成实名认证</summary>
    [HttpPost("online")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> CreateOnlineOrder([FromBody] OnlineOrderDto dto)
    {
        try
        {
            var order = await _svc.CreateOnline(CurrentUserId, dto);
            return Ok(new ApiResponse<object> { Data = OrderResult(order), Message = "线上下单成功，可打印小票" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>线上可租车辆（仅顾客）：只返回空闲车辆的窄字段，不暴露经营数据</summary>
    [HttpGet("available-vehicles")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> SelectCarAndLeaseTime() => Ok(new ApiResponse<List<RentableVehicleDto>> { Data = await _svc.GetRentableVehicles() });

    /// <summary>线上试算：按所选车辆取价，折扣取本人会员等级</summary>
    [HttpGet("estimate")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> GetOrderEstimate(DateTime leaseStart, DateTime leaseEnd, int? vehicleId)
    {
        try { return Ok(new ApiResponse<RentEstimateDto> { Data = await _svc.Estimate(CurrentUserId, leaseStart, leaseEnd, vehicleId) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>线下开单试算：前台为指定客户按其会员等级取折扣</summary>
    [HttpGet("staff-estimate")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> StaffGetOrderEstimate(int customerId, DateTime leaseStart, DateTime leaseEnd, int? vehicleId)
    {
        try { return Ok(new ApiResponse<RentEstimateDto> { Data = await _svc.Estimate(customerId, leaseStart, leaseEnd, vehicleId) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    [HttpPut("{id:int}/cancel")]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public async Task<IActionResult> CancelOrder(int id, [FromBody] string reason)
    {
        try { await _svc.Cancel(id, reason); return Ok(new ApiResponse<object> { Message = "订单已取消" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>顾客自助取消自己的「待支付」订单，立即释放车辆</summary>
    [HttpPost("{id:int}/cancel-pending")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> CancelPendingOrder(int id)
    {
        try
        {
            await _svc.CancelByCustomer(id, CurrentUserId);
            return Ok(new ApiResponse<object> { Message = "订单已取消，车辆已释放" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>顾客申请还车（无论是否到期）</summary>
    [HttpPost("{id:int}/early-return")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> RequestEarlyReturn(int id)
    {
        try
        {
            await _svc.RequestEarlyReturn(id, CurrentUserId);
            return Ok(new ApiResponse<object> { Message = "还车申请已提交，请等待门店审核" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>前台审核还车申请</summary>
    [HttpPost("{id:int}/early-return/review")]
    [Authorize(Policy = Policies.ReceptionOnly)]
    public async Task<IActionResult> ReviewEarlyReturn(int id, [FromBody] EarlyReturnReviewDto dto)
    {
        try
        {
            if (!dto.Approve)
            {
                await _svc.ReviewEarlyReturn(id, false, dto.RejectReason);
                return Ok(new ApiResponse<object> { Message = "已驳回该还车申请" });
            }

            if (dto.DamageFee < 0)
                return BadRequest(new ApiResponse<object> { Code = 400, Message = "损坏费不能为负数", Success = false });
            if (!await _svc.HasPendingEarlyReturn(id))
                return BadRequest(new ApiResponse<object>
                {
                    Code = 400,
                    Message = "该订单没有待审核的还车申请；如需直接收车结算，请走「归还结算」",
                    Success = false
                });

            // 审核通过后立即结算，不再让前台「审核一次、再点一次结算」：
            // 结算事务内部会把仍处于「待审核」的申请单一并关闭；若退款网关不可用导致结算失败，
            // 整笔回滚、申请单仍是「待审核」，前台修好配置后可重试，不会出现"审核过了却没有结算单"。
            // 补缴渠道固定为「待顾客选择」——线下现金还是在线支付，由顾客拿到结算单后自己决定。
            var opId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var result = await _settlement.Settle(new SettleOrderDto
            {
                OrderId = id,
                DamageFee = dto.DamageFee,
                Remark = dto.Remark,
                SupplementPayMethod = SupplementPayMethods.CustomerChoice,
            }, opId);

            return Ok(new ApiResponse<object>
            {
                Data = new
                {
                    result.Record.SettleId,
                    result.Record.ActualRent,
                    result.Record.OverdueFee,
                    result.Record.DamageFee,
                    result.Record.RefundAmount,
                    result.Record.SupplementAmount,
                    NeedCollect = result.Record.SupplementAmount > 0,
                },
                Message = result.Message
            });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
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
    public async Task<IActionResult> GetOrderWarnings(int warnDays = 3) => Ok(new ApiResponse<object> { Data = await _svc.GetWarnings(warnDays) });
}

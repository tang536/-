using System.Security.Claims;
using ERental.DTOs;
using ERental.Models;
using ERental.Services;
using ERental.Authorization;
using ERental.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERental.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = Policies.AnyAuthenticated)]
public class ReportController(ReportService svc, SettlementService settleSvc) : ControllerBase
{
    private readonly ReportService _svc = svc;
    private readonly SettlementService _settleSvc = settleSvc;

    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    /// <summary>首页核心数据看板：管理员 / 前台 / 财务 均可查看</summary>
    [HttpGet("dashboard")]
    [Authorize(Policy = Policies.AnyStaff)]
    public async Task<IActionResult> Dashboard() =>
        Ok(new ApiResponse<VisualReport> { Data = await _svc.GetDashboard() });

    /// <summary>顶栏通知中心 + 首页预警提醒区数据（含当前用户的已读/未读状态）</summary>
    [HttpGet("notifications")]
    [Authorize(Policy = Policies.AnyStaff)]
    public async Task<IActionResult> Notifications() =>
        Ok(new ApiResponse<List<NotificationDto>> { Data = await _svc.GetNotifications(CurrentUserId) });

    /// <summary>
    /// 标记通知已读（用户在通知列表中点开某条后调用）。
    /// 请求体 { ids: ["overdue-12", ...] }；ids 为空表示「全部标记已读」。
    /// </summary>
    [HttpPost("notifications/read")]
    [Authorize(Policy = Policies.AnyStaff)]
    [SkipAudit]   // 通知已读状态属个人阅读偏好，非业务数据变更；高频写日志会淹没真正的业务操作
    public async Task<IActionResult> MarkRead([FromBody] NotificationReadRequest req)
    {
        await _svc.MarkRead(CurrentUserId, req.Ids);
        return Ok(new ApiResponse<object> { Message = "已标记为已读" });
    }

    /// <summary>一键全部已读</summary>
    [HttpPost("notifications/read-all")]
    [Authorize(Policy = Policies.AnyStaff)]
    [SkipAudit]   // 同 MarkRead：阅读偏好，不属业务数据变更
    public async Task<IActionResult> MarkAllRead()
    {
        await _svc.MarkRead(CurrentUserId, null);
        return Ok(new ApiResponse<object> { Message = "已全部标记为已读" });
    }

    /// <summary>
    /// 统计报表主接口（管理员 / 财务）。支持 from / to 时间段筛选：
    /// 财务按任意区间看营收与订单，不传则沿用累计口径。
    /// 角色口径：**财务看全部**（资金 + 运营指标），管理员只取运营指标
    /// （使用率 / 租期 / 取消率等）——资金区块、下钻、对账、支付流水由前端按角色裁剪，
    /// 且下钻 / 对账 / 支付流水接口本身仅对财务授权。
    /// </summary>
    [HttpGet("summary")]
    [Authorize(Policy = Policies.AdminOrFinance)]
    public async Task<IActionResult> Summary(DateTime? from, DateTime? to) =>
        Ok(new ApiResponse<VisualReport> { Data = await _svc.GetDashboard(from, to) });

    /// <summary>
    /// 车辆运营统计（需求 4.2.7）：每台车的累计租赁时长、累计营收、维保次数与维保费用，
    /// 支持按车辆编号 / 品牌关键字与车辆状态筛选，用于识别高价值车辆与闲置车辆。
    /// 权限口径与 summary 一致：管理员 + 财务。
    /// </summary>
    /// <param name="keyword">车辆编号 / 品牌模糊匹配</param>
    /// <param name="status">车辆状态：空闲 / 在租 / 维保 / 报废</param>
    /// <param name="from">统计起始日（含）；不传为累计口径</param>
    /// <param name="to">统计截止日（含当天）</param>
    [HttpGet("vehicles")]
    [Authorize(Policy = Policies.AdminOrFinance)]
    public async Task<IActionResult> VehicleStats(string? keyword, string? status, DateTime? from, DateTime? to)
    {
        try
        {
            return Ok(new ApiResponse<VehicleStatDto> { Data = await _svc.GetVehicleStatsAsync(from, to, keyword, status) });
        }
        catch (Exception ex)
        {
            // 与下钻同处理：明细块失败只让这一块显示「异常 + 重试」，不拖垮整个报表页
            return Ok(new ApiResponse<VehicleStatDto> { Code = 500, Success = false, Message = ErrorText.Safe(ex), Data = null });
        }
    }

    /// <summary>
    /// 维保费用统计（需求 4.2.7）：月度维保总费用、故障类型费用分布、单台车辆维保费用排行。
    /// 权限口径与 summary 一致：管理员 + 财务。
    /// </summary>
    /// <param name="from">统计起始日（含）；不传为累计口径</param>
    /// <param name="to">统计截止日（含当天）</param>
    [HttpGet("maintenance")]
    [Authorize(Policy = Policies.AdminOrFinance)]
    public async Task<IActionResult> MaintenanceStats(DateTime? from, DateTime? to)
    {
        try
        {
            return Ok(new ApiResponse<MaintenanceStatDto> { Data = await _svc.GetMaintenanceStatsAsync(from, to) });
        }
        catch (Exception ex)
        {
            return Ok(new ApiResponse<MaintenanceStatDto> { Code = 500, Success = false, Message = ErrorText.Safe(ex), Data = null });
        }
    }

    /// <summary>
    /// 财务下钻：按「时间 / 订单 / 商户（收单渠道）」拆开看逐笔明细与金额构成。
    ///
    /// 权限口径：**仅财务**。资金逐笔明细是财务的职责范围（财务查看范围 > 管理员）：
    /// 管理员看运营口径（summary 的使用率 / 租期 / 取消率等），逐笔资金与对账归财务。
    /// </summary>
    /// <param name="dim">time（按日/月） / order（按订单） / merchant（按收单渠道）</param>
    /// <param name="from">统计起始日（含）；不传表示不限制起点</param>
    /// <param name="to">统计截止日（含当天）；不传表示不限制终点</param>
    [HttpGet("finance/drill")]
    [Authorize(Policy = Policies.FinanceOnly)]
    public async Task<IActionResult> FinanceDrill(string dim = "time", DateTime? from = null, DateTime? to = null)
    {
        try
        {
            return Ok(new ApiResponse<FinanceDrillDto> { Data = await _svc.GetFinanceDrillAsync(from, to, dim) });
        }
        catch (Exception ex)
        {
            // 下钻是"锦上添花"的分析视图，失败不应让整个报表页白屏：
            // 统一 ApiResponse 失败态，前端据此渲染「异常 + 重试」卡片。
            return Ok(new ApiResponse<FinanceDrillDto>
            {
                Code = 500,
                Success = false,
                Message = ErrorText.Safe(ex),
                Data = null
            });
        }
    }

    /// <summary>财务对账：按日期范围核对应收（订单租金+押金）与实收（支付记录），统计退款/补缴/结算笔数，财务每日打烊前核对账目。仅财务可用（资金核对职责）</summary>
    [HttpGet("reconcile")]
    [Authorize(Policy = Policies.FinanceOnly)]
    public async Task<IActionResult> Reconcile(DateTime from, DateTime to)
    {
        try { return Ok(new ApiResponse<object> { Data = await _settleSvc.Reconcile(from, to) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }
}

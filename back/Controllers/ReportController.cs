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
public class ReportController(ReportService svc, SettlementService settleSvc, ILogger<ReportController> logger) : ControllerBase
{
    private readonly ReportService _svc = svc;
    private readonly SettlementService _settleSvc = settleSvc;
    private readonly ILogger<ReportController> _logger = logger;

    /// <summary>首页核心数据看板：管理员 / 前台 / 财务 均可查看</summary>
    [HttpGet("dashboard")]
    [Authorize(Policy = Policies.AnyStaff)]
    public async Task<IActionResult> Dashboard()
    {
        try
        {
            var data = await DbConcurrency.RetryTransientAsync(() => _svc.GetDashboard(), _logger, "首页看板");
            return Ok(new ApiResponse<VisualReport> { Data = data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "首页看板查询失败（{ExType}）", ex.GetType().Name);
            return StatusCode(500, new ApiResponse<VisualReport> { Code = 500, Success = false, Message = ErrorText.Safe(ex), Data = null });
        }
    }

    // 通知中心（列表 / 已读 / 删除）已统一到 MessageController：
    // 员工与顾客共用同一套接口，接收人类型由 Token 角色决定，这里只保留报表相关端点。

    /// <summary>统计报表主接口（管理员 / 财务）</summary>
    [HttpGet("summary")]
    [Authorize(Policy = Policies.AdminOrFinance)]
    public async Task<IActionResult> QueryReport(DateTime? from, DateTime? to)
    {
        try
        {
            // 读接口对"连接层瞬时故障"整段重试：避免闲置后首次查询命中死连接直接 500
            // （表现为「退款后首次提示服务器内部错误、二次才成功」）。
            var data = await DbConcurrency.RetryTransientAsync(() => _svc.GetDashboard(from, to), _logger, "报表汇总");
            return Ok(new ApiResponse<VisualReport> { Data = data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "报表汇总查询失败（{ExType}）", ex.GetType().Name);
            return StatusCode(500, new ApiResponse<VisualReport> { Code = 500, Success = false, Message = ErrorText.Safe(ex), Data = null });
        }
    }

    /// <summary>车辆运营统计（需求 4</summary>
    [HttpGet("vehicles")]
    [Authorize(Policy = Policies.AdminOrFinance)]
    public async Task<IActionResult> QueryVehicleReport(string? keyword, string? status, DateTime? from, DateTime? to)
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

    /// <summary>维保费用统计（需求 4</summary>
    [HttpGet("maintenance")]
    [Authorize(Policy = Policies.AdminOrFinance)]
    public async Task<IActionResult> QueryMaintenanceReport(DateTime? from, DateTime? to)
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

    /// <summary>财务下钻：按「时间 / 订单 / 商户（收单渠道）」拆开看逐笔明细与金额构成</summary>
    [HttpGet("finance/drill")]
    [Authorize(Policy = Policies.FinanceOnly)]
    public async Task<IActionResult> DrillFinanceReport(string dim = "time", DateTime? from = null, DateTime? to = null)
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

    /// <summary>财务对账：按日期范围核对应收（订单租金+押金）与实收（支付记录），统计退款/补缴/结算笔数，财务每日打烊前核对账目</summary>
    [HttpGet("reconcile")]
    [Authorize(Policy = Policies.FinanceOnly)]
    public async Task<IActionResult> ReconcileFinance(DateTime from, DateTime to)
    {
        try { return Ok(new ApiResponse<object> { Data = await _settleSvc.Reconcile(from, to) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }
}

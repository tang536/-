using ERental.Models;
using ERental.Services;
using ERental.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text;

namespace ERental.Controllers;

[ApiController]
[Route("api/[controller]")]
// ⚠️ 这里刻意只写 [Authorize]（= 仅要求已登录），把角色限制全部放到方法级。
// 原因：ASP.NET Core 会把「类级 + 方法级」的 [Authorize(Roles=...)] 取**并集**
// 合成一条策略（AuthorizationPolicy.CombineAsync 的行为），并不会取交集。
// 若类级写 "管理员,前台,财务"、方法级写 "管理员,财务"，实际生效的是三者并集，
// 前台依然能导出营收报表——角色限制形同虚设。
[Authorize(Policy = Policies.AnyAuthenticated)]
public class PrintController(
    PrintService svc, ReportService reportSvc, OrderService orderSvc) : ControllerBase
{
    private readonly PrintService _svc = svc;
    private readonly ReportService _reportSvc = reportSvc;
    private readonly OrderService _orderSvc = orderSvc;

    private bool IsCustomer => User.IsInRole(AuthService.RoleCustomer);
    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    /// <summary>
    /// 订单小票：随开单使用 → 管理员 / 前台（用况2、用况3）；
    /// 顾客亦可自助打印**本人**订单小票（租车成功后自主打印），越权访问他人订单返回 403。
    /// </summary>
    [HttpGet("order/{orderId:int}")]
    [Authorize(Policy = Policies.AdminOrReceptionOrCustomer)]
    public async Task<IActionResult> OrderReceipt(int orderId)
    {
        try
        {
            if (IsCustomer)
            {
                var order = await _orderSvc.GetById(orderId);
                if (order.CustomerId != CurrentUserId)
                    return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "只能打印本人订单的小票", Success = false });
            }
            var text = await _svc.OrderReceipt(orderId);
            return Ok(new { content = text });
        }
        catch (Exception ex) { return BadRequest(new { message = ErrorText.Safe(ex) }); }
    }

    /// <summary>结算单小票：随归还结算使用 → 前台 / 财务（图2-10 #13；管理员不参与资金办理）</summary>
    [HttpGet("settle/{settleId:int}")]
    [Authorize(Policy = Policies.ReceptionOrFinance)]
    public async Task<IActionResult> SettleReceipt(int settleId)
    {
        try
        {
            var text = await _svc.SettleReceipt(settleId);
            return Ok(new { content = text });
        }
        catch (Exception ex) { return BadRequest(new { message = ErrorText.Safe(ex) }); }
    }

    /// <summary>营收报表导出：统计报表模块 → 仅财务（营收属资金口径，财务查看范围 > 管理员）</summary>
    [HttpGet("export/revenue")]
    [Authorize(Policy = Policies.FinanceOnly)]
    public async Task<IActionResult> ExportRevenueCsv(DateTime? from, DateTime? to)
    {
        var dashboard = await _reportSvc.GetDashboard(from, to);
        var sb = new StringBuilder();
        sb.AppendLine(from.HasValue || to.HasValue ? $"统计区间,{from?.ToString("yyyy-MM-dd") ?? "最早"} ~ {to?.ToString("yyyy-MM-dd") ?? "至今"}" : "统计区间,全部（累计口径）");
        sb.AppendLine("指标,数值");
        sb.AppendLine($"总营收,{dashboard.TotalRevenue:F2}");
        sb.AppendLine($"总订单数,{dashboard.TotalOrders}");
        sb.AppendLine($"在租车辆,{dashboard.RentedCount}");
        sb.AppendLine($"注册客户,{dashboard.TotalCustomers}");
        sb.AppendLine($"维保总费用,{dashboard.TotalMaintenanceFee:F2}");
        sb.AppendLine();
        sb.AppendLine("日期,营收");
        foreach (var kv in dashboard.MonthlyRevenue ?? new Dictionary<string, decimal>())
            sb.AppendLine($"{kv.Key},{kv.Value:F2}");

        return CsvFile(sb.ToString(), "营收报表");
    }

    /// <summary>报表打印视图（系统操作 #25 printReport）：管理员 / 财务，返回带打印样式的 HTML。</summary>
    [HttpGet("report-view")]
    [Authorize(Policy = Policies.AdminOrFinance)]
    public async Task<IActionResult> ReportView(DateTime? from = null, DateTime? to = null) =>
        Content(await _svc.ReportHtml(from, to), "text/html; charset=utf-8");

    /// <summary>订单明细导出：订单查询与打印 → 管理员 / 前台（用况2、用况3）</summary>
    [HttpGet("export/orders")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> ExportOrdersCsv(string? status, string? keyword, DateTime? dateFrom = null, DateTime? dateTo = null) => CsvFile(await _svc.OrderListCsv(status, keyword, dateFrom, dateTo), "订单明细");

    /// <summary>维保记录导出：维保记录查询与打印 → 仅管理员（用况4）</summary>
    [HttpGet("export/maintenance")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> ExportMaintenanceCsv(string? status) => CsvFile(await _svc.MaintenanceListCsv(status), "维保记录");

    /// <summary>统一输出 CSV：前置 UTF-8 BOM，避免 Excel 打开中文乱码</summary>
    private FileContentResult CsvFile(string content, string name)
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(content)).ToArray();
        return File(bytes, "text/csv; charset=utf-8", $"{name}_{DateTime.Now:yyyyMMdd}.csv");
    }
}

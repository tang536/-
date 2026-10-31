using ERental.Data;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace ERental.Services;

public class PrintService(AppDbContext db, ReportService reports)
{
    private readonly AppDbContext _db = db;
    private readonly ReportService _reports = reports;

    /// <summary>报表打印视图（系统操作 #25 printReport）：把统计看板的结果渲染成一张带打印样式的 HTML</summary>
    public async Task<string> ReportHtml(DateTime? from = null, DateTime? to = null)
    {
        var r = await _reports.GetDashboard(from, to);
        var range = from.HasValue || to.HasValue
            ? $"{from?.ToString("yyyy-MM-dd") ?? "—"} 至 {to?.ToString("yyyy-MM-dd") ?? "—"}"
            : "全部数据";
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>经营报表</title>");
        sb.Append("<style>body{font-family:'Microsoft YaHei',Arial,sans-serif;margin:24px;color:#1F2328}");
        sb.Append("h1{font-size:18px;color:#C0392B;border-bottom:2px solid #C0392B;padding-bottom:8px}");
        sb.Append("p.meta{font-size:12px;color:#666}");
        sb.Append("table{border-collapse:collapse;width:100%;margin-top:12px}");
        sb.Append("th,td{border:1px solid #C0392B;padding:6px 10px;font-size:13px;text-align:left}");
        sb.Append("th{background:#FAECE9;width:220px}");
        // 打印时去掉页边距，否则 A4 会把右列切掉
        sb.Append("@media print{body{margin:0}}</style></head><body>");
        sb.Append("<h1>途电驿站租赁系统 · 经营报表</h1>");
        sb.Append($"<p class=\"meta\">统计区间：{range}　生成时间：{DateTime.Now:yyyy-MM-dd HH:mm}</p>");
        sb.Append("<table>");
        void Row(string k, string v) => sb.Append($"<tr><th>{k}</th><td>{v}</td></tr>");
        Row("累计营收（元）", r.TotalRevenue.ToString("N2"));
        Row("累计订单数", r.TotalOrders.ToString());
        Row("客户总数", r.TotalCustomers.ToString());
        Row("维保费用合计（元）", r.TotalMaintenanceFee.ToString("N2"));
        Row("车辆使用率", $"{r.VehicleUsageRate:N1}%");
        Row("平均单笔租期（小时）", r.AvgRentHours.ToString("N1"));
        sb.Append("</table></body></html>");
        return sb.ToString();
    }

    public async Task<string> OrderReceipt(int orderId)
    {
        var order = await _db.RentalOrders
            .Include(o => o.Customer).Include(o => o.Vehicle)
            .FirstOrDefaultAsync(o => o.OrderId == orderId)
            ?? throw new Exception("订单不存在");

        var sb = new StringBuilder();
        sb.AppendLine("========== 电动车租赁订单小票 ==========");
        sb.AppendLine($"订单号：{order.OrderNo}");
        sb.AppendLine($"打印时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("----------------------------------------");
        sb.AppendLine($"客户姓名：{order.Customer?.FullName}");
        sb.AppendLine($"身份证号：{MaskIdCard(order.Customer?.IdCard)}");
        sb.AppendLine($"联系电话：{order.Customer?.ContactPhone}");
        sb.AppendLine("----------------------------------------");
        sb.AppendLine($"车辆编号：{order.Vehicle?.VehicleNo}");
        sb.AppendLine($"车辆品牌：{order.Vehicle?.Brand}");
        sb.AppendLine($"起租时间：{order.LeaseStart:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"到期时间：{order.LeaseEnd:yyyy-MM-dd HH:mm}");
        sb.AppendLine("----------------------------------------");
        sb.AppendLine($"租金合计：¥{order.TotalRent:F2}");
        sb.AppendLine($"押    金：¥{order.Deposit:F2}");
        sb.AppendLine($"应收总额：¥{order.TotalRent + order.Deposit:F2}");
        sb.AppendLine($"订单状态：{order.OrderStatus}");
        sb.AppendLine("----------------------------------------");
        sb.AppendLine("  请妥善保管小票，还车时凭此退押金");
        sb.AppendLine("========================================");
        return sb.ToString();
    }

    public async Task<string> SettleReceipt(int settleId)
    {
        var settle = await _db.SettleRecords
            .Include(s => s.RentalOrder).ThenInclude(o => o!.Customer)
            .Include(s => s.RentalOrder).ThenInclude(o => o!.Vehicle)
            .FirstOrDefaultAsync(s => s.SettleId == settleId)
            ?? throw new Exception("结算记录不存在");

        var sb = new StringBuilder();
        sb.AppendLine("========== 还车结算小票 ==========");
        sb.AppendLine($"结算单号：JS{settle.SettleId:D6}");
        sb.AppendLine($"打印时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("----------------------------------------");
        sb.AppendLine($"订单号：{settle.RentalOrder?.OrderNo}");
        sb.AppendLine($"客户：{settle.RentalOrder?.Customer?.FullName}");
        sb.AppendLine($"车辆：{settle.RentalOrder?.Vehicle?.VehicleNo}");
        sb.AppendLine("----------------------------------------");
        sb.AppendLine($"实际租金：¥{settle.ActualRent:F2}");
        sb.AppendLine($"逾期费用：¥{settle.OverdueFee:F2}");
        // 逾期费是「超出约定还车时间那一段」的租金，属租金的特殊形式，
        // 小票上给一条合计，顾客一眼能看出超时那几小时一共算了多少租金
        sb.AppendLine($"租金合计：¥{settle.ActualRent + settle.OverdueFee:F2}");
        sb.AppendLine($"损坏费用：¥{settle.DamageFee:F2}");
        sb.AppendLine("----------------------------------------");
        // 退款里含「预付租金多退的部分」，不只是押金，所以不能写「退还押金」
        sb.AppendLine($"退还金额：¥{settle.RefundAmount:F2}");
        sb.AppendLine($"补缴金额：¥{settle.SupplementAmount:F2}");
        sb.AppendLine($"结算时间：{settle.SettleTime:yyyy-MM-dd HH:mm}");
        if (!string.IsNullOrEmpty(settle.Remark))
            sb.AppendLine($"备注：{settle.Remark}");
        sb.AppendLine("----------------------------------------");
        sb.AppendLine("  感谢您的使用，欢迎再次光临");
        sb.AppendLine("====================================");
        return sb.ToString();
    }

    /// <summary>订单明细导出（需求 4</summary>
    public async Task<string> OrderListCsv(string? status, string? keyword, DateTime? dateFrom = null, DateTime? dateTo = null)
    {
        var q = _db.RentalOrders.AsNoTracking()
            .Include(o => o.Customer).Include(o => o.Vehicle).AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(o => o.OrderStatus == status);
        // 导出与界面同口径：界面按起租日期圈了范围，导出的明细也必须只含这些单
        if (dateFrom.HasValue) q = q.Where(o => o.LeaseStart >= dateFrom.Value.Date);
        if (dateTo.HasValue) q = q.Where(o => o.LeaseStart < dateTo.Value.Date.AddDays(1));
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var k = keyword.Trim();
            q = q.Where(o => o.OrderNo.Contains(k)
                || (o.Customer != null && (o.Customer.FullName.Contains(k)
                    || (o.Customer.ContactPhone != null && o.Customer.ContactPhone.Contains(k))))
                || (o.Vehicle != null && o.Vehicle.VehicleNo.Contains(k)));
        }

        var rows = await q.OrderByDescending(o => o.CreateTime).ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("订单号,客户姓名,联系电话,车辆编号,车辆品牌,起租时间,到期时间,租金,押金,应收总额,订单状态,创建时间");
        foreach (var o in rows)
        {
            sb.AppendLine(string.Join(',',
                Csv(o.OrderNo), Csv(o.Customer?.FullName), Csv(o.Customer?.ContactPhone),
                Csv(o.Vehicle?.VehicleNo), Csv(o.Vehicle?.Brand),
                o.LeaseStart.ToString("yyyy-MM-dd HH:mm"), o.LeaseEnd.ToString("yyyy-MM-dd HH:mm"),
                o.TotalRent.ToString("F2"), o.Deposit.ToString("F2"),
                (o.TotalRent + o.Deposit).ToString("F2"), Csv(o.OrderStatus),
                o.CreateTime.ToString("yyyy-MM-dd HH:mm")));
        }
        return sb.ToString();
    }

    /// <summary>维保记录导出（需求 4</summary>
    public async Task<string> MaintenanceListCsv(string? status)
    {
        var q = _db.MaintenanceOrders.AsNoTracking().Include(m => m.Vehicle).AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(m => m.Status == status);

        var rows = await q.OrderByDescending(m => m.CreateTime).ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("工单号,车辆编号,车辆品牌,故障类型,故障描述,维修费用,工单状态,上报时间,完成时间");
        foreach (var m in rows)
        {
            sb.AppendLine(string.Join(',',
                Csv(m.WorkOrderNo), Csv(m.Vehicle?.VehicleNo), Csv(m.Vehicle?.Brand),
                Csv(m.FaultType), Csv(m.FaultDescription),
                m.MaintainFee.ToString("F2"), Csv(m.Status),
                m.CreateTime.ToString("yyyy-MM-dd HH:mm"),
                m.FinishTime?.ToString("yyyy-MM-dd HH:mm") ?? ""));
        }
        return sb.ToString();
    }

    /// <summary>CSV 字段转义：含逗号 / 双引号 / 换行时用双引号包裹，内部引号翻倍</summary>
    private static string Csv(string? s)
    {
        var v = s ?? string.Empty;
        return v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? '"' + v.Replace("\"", "\"\"") + '"'
            : v;
    }

    /// <summary>小票上的身份证号脱敏（需求 7</summary>
    /// <summary>身份证脱敏：保留前 4 位与后 4 位，中间按实际长度补 *（小票属易丢失凭证，需求 6</summary>
    internal static string MaskIdCard(string? idCard)
    {
        var s = (idCard ?? string.Empty).Trim();
        if (s.Length == 0) return "—";
        if (s.Length <= 8) return new string('*', s.Length);
        return $"{s[..4]}{new string('*', s.Length - 8)}{s[^4..]}";
    }
}

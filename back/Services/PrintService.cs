using ERental.Data;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace ERental.Services;

public class PrintService(AppDbContext db)
{
    private readonly AppDbContext _db = db;

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

    /// <summary>订单明细导出（需求 4.2.4：订单查询支持订单明细导出）</summary>
    public async Task<string> OrderListCsv(string? status, string? keyword)
    {
        var q = _db.RentalOrders.AsNoTracking()
            .Include(o => o.Customer).Include(o => o.Vehicle).AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(o => o.OrderStatus == status);
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

    /// <summary>维保记录导出（需求 4.2.6：维保记录查询支持导出）</summary>
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

    /// <summary>
    /// 小票上的身份证号脱敏（需求 7.1「去标识化」）。
    /// 小票会被随手丢在柜台、被顾客拍照，打出完整 18 位等于把证件号散播到不可控的地方；
    /// 保留前 4 位与后 4 位，既能让前台核对是本人，又不构成完整证件号。
    /// </summary>
    private static string MaskIdCard(string? idCard)
    {
        var s = (idCard ?? string.Empty).Trim();
        if (s.Length == 0) return "—";
        if (s.Length <= 8) return new string('*', s.Length);
        return $"{s[..4]}{new string('*', s.Length - 8)}{s[^4..]}";
    }
}

using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class ReportService(AppDbContext db, MessageService messages) : INotificationSource
{
    ///<summary>报表日期格式：按天</summary>
    private const string DateFormatDay = "yyyy-MM-dd";

    ///<summary>报表日期格式：按月</summary>
    private const string DateFormatMonth = "yyyy-MM";

    ///<summary>预警类型：维保工单</summary>
    private const string AlertTypeMaintenance = "maintenance";

    private readonly AppDbContext _db = db;
    private readonly MessageService _messages = messages;

    ///<summary>统计看板</summary>
    public async Task<VisualReport> GetDashboard(DateTime? from = null, DateTime? to = null)
    {
        var today = DateTime.Now.Date;
        var tomorrow = today.AddDays(1);
        var now = DateTime.Now;
        var soon = now.AddHours(24);

        //区间右端点取「当日 23:59:59」：日期选择器给的是当天 00:00
        var rangeFrom = from?.Date;
        var rangeTo = to?.Date.AddDays(1);
        var hasRange = rangeFrom.HasValue && rangeTo.HasValue;

        //落在区间内的支付/订单
        var payQ = _db.PaymentRecords.Where(p => PayStatuses.MoneyArrived.Contains(p.PayStatus));
        var orderQ = _db.RentalOrders.AsQueryable();
        if (hasRange)
        {
            payQ = payQ.Where(p => p.PayTime.HasValue && p.PayTime >= rangeFrom && p.PayTime < rangeTo);
            orderQ = orderQ.Where(o => o.CreateTime >= rangeFrom && o.CreateTime < rangeTo);
        }

        //趋势先填进本地字典，再随对象一次性构造
        var monthlyRevenue = new Dictionary<string, decimal>();
        var orderTrend = new Dictionary<string, int>();

        if (hasRange)
        {
            var days = (int)(rangeTo!.Value.Date - rangeFrom!.Value.Date).TotalDays;
            //区间 ≤31 天按天铺点
            if (days <= 31) await FillDailyTrendAsync(rangeFrom.Value.Date, rangeTo.Value.Date, monthlyRevenue, orderTrend);
            else await FillMonthlyTrendAsync(rangeFrom.Value.Date, rangeTo.Value.Date, monthlyRevenue, orderTrend);
        }
        else
        {
            //无区间：最近 7 天
            await FillDailyTrendAsync(DateTime.Now.AddDays(-6).Date, DateTime.Now.Date.AddDays(1), monthlyRevenue, orderTrend);
        }

        //车辆状态分布
        var vehicleStatusStats = await _db.Vehicles
            .GroupBy(v => v.VehicleStatus).ToDictionaryAsync(g => g.Key, g => g.Count());

        var draft = new VisualReport
        {
            //累计指标（指定区间时即为「区间内」指标）
            TotalRevenue = await payQ.SumAsync(p => p.PayAmount),
            TotalOrders = await orderQ.CountAsync(),
            TotalCustomers = await _db.Customers.CountAsync(),
            TotalMaintenanceFee = await _db.MaintenanceOrders.Where(m => m.Status == "已完成").SumAsync(m => m.MaintainFee),
            VehicleStatusStats = vehicleStatusStats,
            OrderStatusStats = await orderQ.GroupBy(o => o.OrderStatus).ToDictionaryAsync(g => g.Key, g => g.Count()),

            //首页看板指标
            TodayRevenue = await _db.PaymentRecords.Where(p => PayStatuses.MoneyArrived.Contains(p.PayStatus) && p.PayTime.HasValue && p.PayTime >= today && p.PayTime < tomorrow).SumAsync(p => p.PayAmount),
            TodayOrders = await _db.RentalOrders.CountAsync(o => o.CreateTime >= today && o.CreateTime < tomorrow),
            RentedCount = vehicleStatusStats.GetValueOrDefault("在租"),
            FreeCount = vehicleStatusStats.GetValueOrDefault("空闲"),
            ActiveOrderCount = await _db.RentalOrders.CountAsync(o => o.OrderStatus == "已支付" || o.OrderStatus == "租赁中"),
            OverdueCount = await _db.RentalOrders.CountAsync(o => o.LeaseEnd < now && o.OrderStatus != "已完结" && o.OrderStatus != "已取消"),
            ExpiringCount = await _db.RentalOrders.CountAsync(o => o.LeaseEnd >= now && o.LeaseEnd <= soon && o.OrderStatus != "已完结" && o.OrderStatus != "已取消"),
            PendingPayCount = await _db.RentalOrders.CountAsync(o => o.OrderStatus == "待支付"),
            MaintenanceCount = vehicleStatusStats.GetValueOrDefault("维保"),
            IncompleteProfileCount = await _db.Customers.CountAsync(c => c.IdCard == null || c.IdCard == "" || c.ContactPhone == null || c.ContactPhone == ""),
            //保养到期
            MaintenanceDueCount = (await GetMaintenanceDueAsync()).Count,
            //营收趋势：无区间给最近 7 天
            MonthlyRevenue = monthlyRevenue,
            OrderTrend = orderTrend
        };

        //运营指标取报表自身的累计值与订单状态分布来算
        var metrics = await ComputeOperationMetrics(orderQ, draft);
        return draft with
        {
            TotalVehicleCount = metrics.TotalVehicleCount,
            RentedVehicleCount = metrics.RentedVehicleCount,
            VehicleUsageRate = metrics.VehicleUsageRate,
            AvgRentHours = metrics.AvgRentHours,
            RevenuePerVehicle = metrics.RevenuePerVehicle,
            CancelRate = metrics.CancelRate
        };
    }

    ///<summary>计算管理员端专属的运营指标</summary>
    private static async Task<OperationMetrics> ComputeOperationMetrics(IQueryable<RentalOrder> orderQ, VisualReport report)
    {
        //必须取回内存再算：DateTime 相减在 EF 里翻译不了
        var orders = await orderQ.Select(o => new { o.OrderStatus, o.VehicleId, o.LeaseStart, o.LeaseEnd }).ToListAsync();

        //车辆总数直接取报表里的状态分布合计：口径与前端饼图一致，且不再额外查一次库
        var totalVehicleCount = report.VehicleStatusStats?.Values.Sum() ?? 0;

        //使用率分母恒为「全部车辆」：运营关心的是车队整体周转，不是某个区间的存量
        var rentedVehicleCount = orders.Select(x => x.VehicleId).Distinct().Count();
        var vehicleUsageRate = totalVehicleCount == 0 ? 0m : Math.Round(rentedVehicleCount * 100m / totalVehicleCount, 1);

        //取消率直接取报表里的订单状态分布，保证与前端柱状图看到的是同一份数
        var statusTotal = report.OrderStatusStats?.Values.Sum() ?? 0;
        var canceled = report.OrderStatusStats?.GetValueOrDefault("已取消") ?? 0;
        var cancelRate = statusTotal == 0 ? 0m : Math.Round(canceled * 100m / statusTotal, 1);

        //LeaseEnd/LeaseStart 均为非空 DateTime，无需判空
        var durations = orders.Where(x => x.LeaseEnd > x.LeaseStart).Select(x => (x.LeaseEnd - x.LeaseStart).TotalHours).ToList();
        var avgRentHours = durations.Count == 0 ? 0m : Math.Round((decimal)durations.Average(), 1);

        var revenuePerVehicle = totalVehicleCount == 0 ? 0m : Math.Round(report.TotalRevenue / totalVehicleCount, 2);

        return new OperationMetrics(totalVehicleCount, rentedVehicleCount, vehicleUsageRate, avgRentHours, revenuePerVehicle, cancelRate);
    }

    ///<summary>运营指标的中间结果：算完一次性交给报表，不再回头改报表字段</summary>
    private sealed record OperationMetrics(
        int TotalVehicleCount,
        int RentedVehicleCount,
        decimal VehicleUsageRate,
        decimal AvgRentHours,
        decimal RevenuePerVehicle,
        decimal CancelRate);

    //财务下钻支持的维度
    public const string DimTime = "time";
    public const string DimOrder = "order";
    public const string DimMerchant = "merchant";

    ///<summary>财务下钻：按「时间/订单/商户」三个维度拆开看金额构成与逐笔明细</summary>
    public async Task<FinanceDrillDto> GetFinanceDrillAsync(DateTime? from, DateTime? to, string? dim)
    {
        var dimension = NormalizeDimension(dim);

        //区间右端点取「次日 00
        var (rangeFrom, rangeTo, hasRange) = ResolveDateRange(from, to);

        //实收口径与对账页严格一致：只数订单支付（biz_type='order'）里「钱到过账」的记录
        var payQ = _db.PaymentRecords.Where(p => p.BizType == "order" && PayStatuses.MoneyArrived.Contains(p.PayStatus) && p.PayTime.HasValue);
        var orderQ = _db.RentalOrders.AsQueryable();
        if (hasRange)
        {
            payQ = payQ.Where(p => p.PayTime >= rangeFrom && p.PayTime < rangeTo);
            orderQ = orderQ.Where(o => o.CreateTime >= rangeFrom && o.CreateTime < rangeTo);
        }

        //先落内存
        var pays = await payQ.ToListAsync();
        var orders = await orderQ.ToListAsync();
        //订单维度要显示客户/车牌，单独带出（不影响上面两个集合）
        var orderExtras = dimension == DimOrder
            ? await LoadOrderExtrasAsync(orderQ)
            : new Dictionary<int, (string Customer, string Vehicle, string Status)>();

        var dto = new FinanceDrillDto
        {
            Dimension = dimension,
            DimensionName = DimensionDisplayName(dimension),
            From = from?.Date.ToString(DateFormatDay) ?? "",
            To = to?.Date.ToString(DateFormatDay) ?? "",
            TotalReceived = pays.Sum(p => p.PayAmount),
            TotalRefund = pays.Sum(p => p.RefundAmount),
            PayCount = pays.Count,
            OrderCount = orders.Count,
            TotalRent = orders.Sum(o => o.TotalRent),
            TotalDeposit = orders.Sum(o => o.Deposit)
        };
        dto.NetIncome = Math.Round(dto.TotalReceived - dto.TotalRefund, 2);

        //订单号 → 该订单的实收/退款，供「按订单」与「按时间」两个维度复用
        var payByOrder = AggregatePayByOrder(pays);

        switch (dimension)
        {
            case DimOrder:
                dto.Columns = ["订单号", "客户 / 车辆 / 状态", "应收租金", "应收押金", "实收", "退款", "净额"];
                dto.Rows = BuildOrderDrillRows(orders, payByOrder, orderExtras);
                break;

            case DimMerchant:
                dto.Columns = ["收单渠道", "关联订单", "支付笔数", "实收", "退款", "净额", "占比"];
                dto.Rows = BuildMerchantDrillRows(pays, dto.TotalReceived);
                break;

            default: //time
                var (timeColumns, timeRows) = BuildTimeDrillSection(pays, hasRange, rangeFrom, rangeTo);
                dto.Columns = timeColumns;
                dto.Rows = timeRows;
                break;
        }

        return dto;
    }

    ///<summary>维度参数归一到三个受支持值：大小写/空格容错，未知值一律按「时间」处理（保持旧行为）</summary>
    private static string NormalizeDimension(string? dim)
    {
        var dimension = (dim ?? DimTime).Trim().ToLowerInvariant();
        return dimension is DimTime or DimOrder or DimMerchant ? dimension : DimTime;
    }

    ///<summary>报表区间统一解析：右端点取「次日 00:00」</summary>
    private static (DateTime? From, DateTime? To, bool HasRange) ResolveDateRange(DateTime? from, DateTime? to)
    {
        var rangeFrom = from?.Date;
        var rangeTo = to?.Date.AddDays(1);
        return (rangeFrom, rangeTo, rangeFrom.HasValue && rangeTo.HasValue);
    }

    ///<summary>维度显示名</summary>
    private static string DimensionDisplayName(string dimension) => dimension switch
    {
        DimOrder => "按订单",
        DimMerchant => "按商户（收单渠道）",
        _ => "按时间"
    };

    ///<summary>订单维度用的客户/车牌/状态，按订单号索引</summary>
    private static async Task<Dictionary<int, (string Customer, string Vehicle, string Status)>> LoadOrderExtrasAsync(IQueryable<RentalOrder> orderQ)
    {
        var extra = await orderQ.Select(o => new
        {
            o.OrderId,
            o.OrderNo,
            o.OrderStatus,
            Customer = o.Customer != null ? o.Customer.FullName : "",
            Vehicle = o.Vehicle != null ? o.Vehicle.VehicleNo : ""
        })
            .ToListAsync();

        var orderExtras = new Dictionary<int, (string Customer, string Vehicle, string Status)>();
        foreach (var x in extra)
            orderExtras[x.OrderId] = (x.Customer, x.Vehicle, x.OrderStatus);
        return orderExtras;
    }

    ///<summary>按订单汇总实收/退款</summary>
    private static Dictionary<int, PayAgg> AggregatePayByOrder(List<PaymentRecord> pays) =>
        pays.GroupBy(p => p.OrderId)
            .ToDictionary(g => g.Key, g => new PayAgg
            {
                Received = g.Sum(x => x.PayAmount),
                Refund = g.Sum(x => x.RefundAmount)
            });

    ///<summary>订单维度明细：一单一行，按创建时间倒序</summary>
    private static List<DrillRowDto> BuildOrderDrillRows(
        List<RentalOrder> orders,
        Dictionary<int, PayAgg> payByOrder,
        Dictionary<int, (string Customer, string Vehicle, string Status)> orderExtras)
    {
        var rows = new List<DrillRowDto>();
        foreach (var o in orders.OrderByDescending(o => o.CreateTime))
        {
            var got = payByOrder.TryGetValue(o.OrderId, out var v) ? v : new PayAgg();
            orderExtras.TryGetValue(o.OrderId, out var ex);
            rows.Add(new DrillRowDto
            {
                Key = o.OrderNo,
                Label = o.OrderNo,
                OrderCount = 1,
                PayCount = got.Received > 0 ? 1 : 0,
                Rent = o.TotalRent,
                Deposit = o.Deposit,
                Received = got.Received,
                Refund = got.Refund,
                Net = Math.Round(got.Received - got.Refund, 2),
                Ext = $"{ex.Customer} / {ex.Vehicle} / {ex.Status}"
            });
        }
        return rows;
    }

    ///<summary>商户维度明细：商户=收单渠道（支付宝/微信支付/银行卡）</summary>
    private static List<DrillRowDto> BuildMerchantDrillRows(List<PaymentRecord> pays, decimal totalReceived)
    {
        var rows = new List<DrillRowDto>();
        var byChannel = pays.GroupBy(p => string.IsNullOrWhiteSpace(p.PayType) ? "未标注" : p.PayType);
        foreach (var g in byChannel.OrderByDescending(g => g.Sum(x => x.PayAmount)))
        {
            var recv = g.Sum(x => x.PayAmount);
            var refund = g.Sum(x => x.RefundAmount);
            rows.Add(new DrillRowDto
            {
                Key = g.Key,
                Label = g.Key,
                PayCount = g.Count(),
                OrderCount = g.Select(x => x.OrderId).Distinct().Count(),
                Received = recv,
                Refund = refund,
                Net = Math.Round(recv - refund, 2),
                Share = totalReceived == 0 ? 0 : Math.Round(recv * 100m / totalReceived, 1)
            });
        }
        return rows;
    }

    ///<summary>时间维度明细</summary>
    private static (List<string> Columns, List<DrillRowDto> Rows) BuildTimeDrillSection(
        List<PaymentRecord> pays, bool hasRange, DateTime? rangeFrom, DateTime? rangeTo)
    {
        //区间超过 31 天按天铺会出几百行
        var spanDays = hasRange ? (int)(rangeTo!.Value.Date - rangeFrom!.Value.Date).TotalDays : 7;
        var byMonth = spanDays > 31;
        var buckets = pays
            .GroupBy(p => byMonth ? p.PayTime!.Value.ToString(DateFormatMonth) : p.PayTime!.Value.ToString("MM-dd"))
            .OrderBy(g => g.Key);

        var rows = new List<DrillRowDto>();
        foreach (var g in buckets)
        {
            var recv = g.Sum(x => x.PayAmount);
            var refund = g.Sum(x => x.RefundAmount);
            rows.Add(new DrillRowDto
            {
                Key = g.Key,
                Label = g.Key,
                PayCount = g.Count(),
                OrderCount = g.Select(x => x.OrderId).Distinct().Count(),
                Received = recv,
                Refund = refund,
                Net = Math.Round(recv - refund, 2)
            });
        }

        List<string> columns = [byMonth ? "月份" : "日期", "关联订单", "支付笔数", "实收", "退款", "净额"];
        return (columns, rows);
    }

    ///<summary>某笔订单的收款聚合</summary>
    private sealed class PayAgg
    {
        public decimal Received { get; init; }
        public decimal Refund { get; init; }
    }

    ///<summary>把员工侧的实时预警同步进通用 message 表</summary>
    public async Task SyncAsync(string receiverType, int receiverId)
    {
        //实时预警只对员工有意义：顾客消息来自业务事件（退款/补缴），不靠状态推导
        if (receiverType != "staff") return;

        //① 实时预警
        var alerts = await BuildNotifications();
        var liveGlobal = new HashSet<string>();
        foreach (var a in alerts)
        {
            var (refType, refId) = SplitAlertId(a.Id);
            //本轮真正触发的全局预警（维保中/资料待完善/保养到期），供下一步清理旧行用
            if (GlobalAlertTypes.Contains(refType)) liveGlobal.Add(refType);
            //KeepReadStateOnChange：实时预警的标题含「已逾期 N 小时」这类会自己走的时长
            await _messages.SendAsync(
                ("staff", receiverId),
                (refType, refId),
                (a.Title, a.Description),
                (a.Level, a.Link, true));
        }

        //② 清掉已经失效的实时预警行
        var liveKeys = await AlertLiveKeysAsync(liveGlobal);

        //只取主键与业务键两列：清理只需要判定「哪些行已经没有对应预警」
        var realtimeRows = await _db.Messages.AsNoTracking().Where(m => m.ReceiverType == "staff" && m.ReceiverId == receiverId && RealtimeAlertTypes.Contains(m.RefType)).Select(m => new { m.MessageId, m.RefType, m.RefId }).ToListAsync();

        var deadIds = realtimeRows.Where(m => !liveKeys.Contains(AlertKey(m.RefType, m.RefId))).Select(m => m.MessageId).ToList();

        if (deadIds.Count > 0)
        {
            //一条 DELETE ... IN(...) 结束，不再逐条挂到跟踪器上再 SaveChanges
            await _db.Messages.Where(m => deadIds.Contains(m.MessageId)).ExecuteDeleteAsync();
        }
    }

    ///<summary>全局聚合类预警的 ref_id 固定值（不能为空：唯一索引里 NULL 互不相等，会反复插新行）</summary>
    private const string GlobalRefId = "global";

    ///<summary>message 行业务键：与前端拿到的 Id（ref_type-ref_id）保持同一套拼法</summary>
    private static string AlertKey(string? refType, string? refId) => $"{refType}-{refId ?? string.Empty}";

    ///<summary>本轮「应当存在」的实时预警键集合</summary>
    private async Task<HashSet<string>> AlertLiveKeysAsync(HashSet<string> liveGlobal)
    {
        var now = DateTime.Now;
        var soon = now.AddHours(24);
        var live = new HashSet<string>();

        var orders = await _db.RentalOrders.AsNoTracking().Where(o => o.OrderStatus != "已完结" && o.OrderStatus != "已取消").Select(o => new { o.OrderId, o.LeaseEnd, o.OrderStatus }).ToListAsync();

        foreach (var o in orders)
        {
            var id = o.OrderId.ToString();
            if (o.LeaseEnd < now) live.Add(AlertKey("overdue", id));
            else if (o.LeaseEnd <= soon) live.Add(AlertKey("expiring", id));
            if (o.OrderStatus == "待支付") live.Add(AlertKey("pending", id));
        }

        foreach (var t in liveGlobal) live.Add(AlertKey(t, GlobalRefId));
        return live;
    }

    private static readonly HashSet<string> GlobalAlertTypes = [AlertTypeMaintenance, "maintain_due", "profile_incomplete"];

    ///<summary>由实时状态推导的预警类型：都随业务状态变化而失效，需要按轮次清理</summary>
    private static readonly HashSet<string> RealtimeAlertTypes = ["overdue", "expiring", "pending", AlertTypeMaintenance, "maintain_due", "profile_incomplete"];

    ///<summary>把预警的稳定字符串 ID（overdue-12/maintain-due 等）拆成</summary>
    private static (string RefType, string RefId) SplitAlertId(string id) => id switch
    {
        /*
         * 全局聚合类预警（维保中车辆数/待完善资料数/保养到期数）没有业务实体，
         * 归一到固定的 ref_id="global"。
         *
         * 这里不能像以前那样给空串：SendAsync 会把空串还原成 NULL，而 uk_msg_biz
         * 的唯一索引里 NULL 彼此互不相等 —— MySQL 认为(staff,1,maintenance,NULL) 每条都不一样，
         * ON DUPLICATE KEY 永远不命中，于是每轮 60 秒轮询都插一条新消息，
         * 表现就是铃铛未读数一路狂涨、列表里全是「有 N 台车辆处于维保中」。
         */
        AlertTypeMaintenance => (AlertTypeMaintenance, GlobalRefId),
        "profile-incomplete" => ("profile_incomplete", GlobalRefId),
        "maintain-due" => ("maintain_due", GlobalRefId),
        _ => SplitPrefixedId(id)
    };

    private static (string RefType, string RefId) SplitPrefixedId(string id)
    {
        var i = id.IndexOf('-');
        return i > 0 ? (id[..i], id[(i + 1)..]) : (id, "");
    }

    ///<summary>实时计算通知条目（不含已读状态，已读由 MessageService 读表时合并）</summary>
    private async Task<List<NotificationDto>> BuildNotifications()
    {
        var now = DateTime.Now;
        var list = new List<NotificationDto>();

        //订单类预警：逾期/即将到期/待支付
        list.AddRange(await BuildOverdueNotificationsAsync(now));
        list.AddRange(await BuildExpiringNotificationsAsync(now));
        list.AddRange(await BuildPendingNotificationsAsync());

        //存量类预警：维保中车辆/资料待完善/保养到期
        var maintaining = await BuildMaintainingNotificationAsync(now);
        if (maintaining != null) list.Add(maintaining);
        var incomplete = await BuildIncompleteProfileNotificationAsync(now);
        if (incomplete != null) list.Add(incomplete);
        var maintainDue = await BuildMaintenanceDueNotificationAsync(now);
        if (maintainDue != null) list.Add(maintainDue);

        //按时间倒序（稳定排序）：同一时刻的多条预警保持上面的插入顺序，拆分前后前端铃铛里的排列完全一致
        return [.. list.OrderByDescending(n => n.Time)];
    }

    ///<summary>1. 逾期未归还：取最紧急的 20 单，按到期时间升序</summary>
    private async Task<List<NotificationDto>> BuildOverdueNotificationsAsync(DateTime now)
    {
        //联表与到期字段都在订单总览视图里，不再 Include 客户/车辆整实体
        var overdue = await _db.OrderOverviews.AsNoTracking()
            .Where(o => o.LeaseEnd < now && o.OrderStatus != "已完结" && o.OrderStatus != "已取消")
            .OrderBy(o => o.LeaseEnd)
            .Take(20)
            .Select(o => new { o.OrderId, o.OrderNo, o.CustomerName, o.VehicleNo, o.LeaseEnd })
            .ToListAsync();

        var list = new List<NotificationDto>();
        foreach (var o in overdue)
        {
            list.Add(new NotificationDto
            {
                Id = $"overdue-{o.OrderId}",
                Level = "danger",
                Category = "逾期预警",
                Title = $"订单 {o.OrderNo} 已逾期 {DescribeOverdueSpan(now - o.LeaseEnd)}",
                Description = $"客户：{o.CustomerName}\u3000车辆：{o.VehicleNo}\u3000到期：{o.LeaseEnd:MM-dd HH:mm}，请尽快联系客户跟进",
                Time = o.LeaseEnd,
                Link = "/orders"
            });
        }
        return list;
    }

    ///<summary>逾期时长文案：满一天按「X 天 Y 小时」、不足一天按小时、再不足按分钟</summary>
    private static string DescribeOverdueSpan(TimeSpan span)
    {
        var fullDays = (int)span.TotalDays;//满 24 小时才算一天
        var restHours = span.Hours;//不足一天的零头
        return fullDays switch
        {
            > 0 when restHours > 0 => $"{fullDays} 天 {restHours} 小时",
            > 0 => $"{fullDays} 天",
            _ when span.TotalHours >= 1 => $"{(int)span.TotalHours} 小时",
            _ => $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} 分钟"
        };
    }

    ///<summary>2. 24 小时内到期</summary>
    private async Task<List<NotificationDto>> BuildExpiringNotificationsAsync(DateTime now)
    {
        var soon = now.AddHours(24);
        var expiring = await _db.OrderOverviews.AsNoTracking()
            .Where(o => o.LeaseEnd >= now && o.LeaseEnd <= soon && o.OrderStatus != "已完结" && o.OrderStatus != "已取消")
            .OrderBy(o => o.LeaseEnd)
            .Take(20)
            .Select(o => new { o.OrderId, o.OrderNo, o.CustomerName, o.VehicleNo, o.LeaseEnd })
            .ToListAsync();

        var list = new List<NotificationDto>();
        foreach (var o in expiring)
        {
            list.Add(new NotificationDto
            {
                Id = $"expiring-{o.OrderId}",
                Level = "warning",
                Category = "到期提醒",
                Title = $"订单 {o.OrderNo} 即将到期",
                Description = $"客户：{o.CustomerName}\u3000车辆：{o.VehicleNo}\u3000到期：{o.LeaseEnd:MM-dd HH:mm}",
                Time = o.LeaseEnd,
                Link = "/orders"
            });
        }
        return list;
    }

    ///<summary>3. 待支付订单：按下单时间倒序</summary>
    private async Task<List<NotificationDto>> BuildPendingNotificationsAsync()
    {
        var pending = await _db.OrderOverviews.AsNoTracking()
            .Where(o => o.OrderStatus == "待支付")
            .OrderByDescending(o => o.CreateTime)
            .Take(20)
            .Select(o => new { o.OrderId, o.OrderNo, o.CustomerName, o.TotalRent, o.Deposit, o.CreateTime })
            .ToListAsync();

        var list = new List<NotificationDto>();
        foreach (var o in pending)
        {
            list.Add(new NotificationDto
            {
                Id = $"pending-{o.OrderId}",
                Level = "info",
                Category = "待支付",
                Title = $"订单 {o.OrderNo} 待支付",
                Description = $"客户：{o.CustomerName}\u3000金额：¥{o.TotalRent + o.Deposit:F2}",
                Time = o.CreateTime,
                Link = "/orders"
            });
        }
        return list;
    }

    ///<summary>4. 维保中车辆：全店聚合成一条，无维保车时不出条目</summary>
    private async Task<NotificationDto?> BuildMaintainingNotificationAsync(DateTime now)
    {
        var maintaining = await _db.Vehicles.AsNoTracking()
            .Where(v => v.VehicleStatus == "维保")
            .OrderBy(v => v.VehicleNo)
            .Take(20)
            .Select(v => v.VehicleNo)
            .ToListAsync();
        if (maintaining.Count == 0) return null;

        return new NotificationDto
        {
            Id = AlertTypeMaintenance,
            Level = "warning",
            Category = "维保提醒",
            Title = $"有 {maintaining.Count} 台车辆处于维保中",
            Description = string.Join("、", maintaining),
            Time = now,
            Link = "/maintenance"
        };
    }

    ///<summary>5. 客户资料待完善（身份证号/手机号未采集）</summary>
    private async Task<NotificationDto?> BuildIncompleteProfileNotificationAsync(DateTime now)
    {
        var incompleteCount = await _db.Customers.CountAsync(c => c.IdCard == null || c.IdCard == "" || c.ContactPhone == null || c.ContactPhone == "");
        if (incompleteCount == 0) return null;

        return new NotificationDto
        {
            Id = "profile-incomplete",
            Level = "info",
            Category = "资料待完善",
            Title = $"有 {incompleteCount} 位客户待完善租车资料",
            Description = "注册时仅采集用户名与密码，租车下单前需补录身份证号与手机号",
            Time = now,
            Link = "/customers"
        };
    }

    ///<summary>6. 保养提醒：周期与到期判定由 v_vehicle_maintenance_due 在库内算好</summary>
    private async Task<NotificationDto?> BuildMaintenanceDueNotificationAsync(DateTime now)
    {
        var maintainCycleDays = ConfigValue.Int(await _db.SystemConfigs.ToListAsync(), "maintain_cycle_days", 90);
        if (maintainCycleDays <= 0) return null;

        var due = await GetMaintenanceDueAsync();
        if (due.Count == 0) return null;

        return new NotificationDto
        {
            Id = "maintain-due",
            Level = "warning",
            Category = "保养提醒",
            Title = $"有 {due.Count} 台车辆已超过 {maintainCycleDays} 天保养周期",
            Description = string.Join("、", due.Take(10).Select(d => $"{d.VehicleNo}（已 {d.Days} 天）"))
                + (due.Count > 10 ? $" 等 {due.Count} 台" : ""),
            Time = now,
            Link = "/maintenance"
        };
    }

    ///<summary>保养到期车辆清单：基准时间、周期与到期标志全部由数据库视图计算</summary>
    private async Task<List<(string VehicleNo, int Days)>> GetMaintenanceDueAsync()
    {
        var due = await _db.VehicleMaintenanceDues.AsNoTracking()
            .Where(v => v.IsDue == 1)
            .OrderByDescending(v => v.DaysSince)
            .Select(v => new { v.VehicleNo, Days = v.DaysSince })
            .ToListAsync();

        return due.Select(v => (v.VehicleNo, v.Days ?? 0)).ToList();
    }

    //车辆运营统计/维保费用统计

    ///<summary>车辆运营统计</summary>
    public async Task<VehicleStatDto> GetVehicleStatsAsync(DateTime? from, DateTime? to, string? keyword, string? status)
    {
        var (rangeFrom, rangeTo, hasRange) = ResolveDateRange(from, to);

        //三张表各按区间取一次数，再在内存里按车辆拼起来
        var usage = await AggregateOrderUsageAsync(rangeFrom, rangeTo, hasRange);
        var revenue = await AggregateRevenueByVehicleAsync(rangeFrom, rangeTo, hasRange, usage.OrderVehicle);
        var maint = await AggregateMaintenanceByVehicleAsync(rangeFrom, rangeTo, hasRange);

        //先铺满全部车辆再筛选：区间内没出过车的车也要留在列表里（IdleVehicles 靠它统计）
        var rows = FilterVehicleStatRows(await BuildVehicleStatRowsAsync(usage, revenue, maint), keyword, status)
            //营收高的排前面
            .OrderByDescending(r => r.Revenue).ThenBy(r => r.VehicleNo)
            .ToList();

        return new VehicleStatDto
        {
            From = rangeFrom?.ToString(DateFormatDay) ?? "",
            To = to?.ToString(DateFormatDay) ?? "",
            TotalVehicles = rows.Count,
            IdleVehicles = rows.Count(r => r.OrderCount == 0),
            TotalRentHours = Math.Round(rows.Sum(r => r.RentHours), 1),
            TotalRevenue = Math.Round(rows.Sum(r => r.Revenue), 2),
            TotalMaintenanceCount = rows.Sum(r => r.MaintenanceCount),
            TotalMaintenanceFee = Math.Round(rows.Sum(r => r.MaintenanceFee), 2),
            Rows = rows
        };
    }

    ///<summary>订单侧聚合：每车的订单数/累计租赁小时/最近一次起租，并顺带产出「订单号 → 车辆」映射</summary>
    private async Task<(Dictionary<int, int> OrderCount, Dictionary<int, decimal> Hours, Dictionary<int, DateTime> LastRent, Dictionary<int, int> OrderVehicle)> AggregateOrderUsageAsync(
        DateTime? rangeFrom, DateTime? rangeTo, bool hasRange)
    {
        var orderQ = _db.RentalOrders.AsNoTracking().AsQueryable();
        if (hasRange) orderQ = orderQ.Where(o => o.CreateTime >= rangeFrom && o.CreateTime < rangeTo);
        var orders = await orderQ.Select(o => new { o.OrderId, o.VehicleId, o.LeaseStart, o.LeaseEnd }).ToListAsync();

        var orderCount = new Dictionary<int, int>();
        var hours = new Dictionary<int, decimal>();
        var lastRent = new Dictionary<int, DateTime>();
        foreach (var o in orders)
        {
            orderCount[o.VehicleId] = orderCount.GetValueOrDefault(o.VehicleId) + 1;
            if (o.LeaseEnd > o.LeaseStart) hours[o.VehicleId] = hours.GetValueOrDefault(o.VehicleId) + (decimal)(o.LeaseEnd - o.LeaseStart).TotalHours;
            if (!lastRent.TryGetValue(o.VehicleId, out var t) || o.LeaseStart > t) lastRent[o.VehicleId] = o.LeaseStart;
        }

        //营收按「订单所租车辆」归属：一笔流水挂的是订单，车辆要经订单一跳才能对上
        var orderVehicle = orders.ToDictionary(o => o.OrderId, o => o.VehicleId);
        return (orderCount, hours, lastRent, orderVehicle);
    }

    ///<summary>营收按车辆归集：流水挂的是订单号，先用订单映射换到车辆</summary>
    private async Task<Dictionary<int, decimal>> AggregateRevenueByVehicleAsync(
        DateTime? rangeFrom, DateTime? rangeTo, bool hasRange, Dictionary<int, int> orderVehicle)
    {
        var payQ = _db.PaymentRecords.AsNoTracking().Where(p => PayStatuses.MoneyArrived.Contains(p.PayStatus) && p.PayTime.HasValue);
        if (hasRange) payQ = payQ.Where(p => p.PayTime >= rangeFrom && p.PayTime < rangeTo);
        var pays = await payQ.Select(p => new { p.OrderId, p.PayAmount }).ToListAsync();

        var revenue = new Dictionary<int, decimal>();
        foreach (var p in pays)
        {
            if (orderVehicle.TryGetValue(p.OrderId, out var vid)) revenue[vid] = revenue.GetValueOrDefault(vid) + p.PayAmount;
        }
        return revenue;
    }

    ///<summary>维保按车辆归集：次数统计全部工单（含未完工），费用只累计「已完成」</summary>
    private async Task<(Dictionary<int, int> Count, Dictionary<int, decimal> Fee)> AggregateMaintenanceByVehicleAsync(
        DateTime? rangeFrom, DateTime? rangeTo, bool hasRange)
    {
        var maintQ = _db.MaintenanceOrders.AsNoTracking().AsQueryable();
        if (hasRange) maintQ = maintQ.Where(m => m.CreateTime >= rangeFrom && m.CreateTime < rangeTo);
        var maints = await maintQ.Select(m => new { m.VehicleId, m.Status, m.MaintainFee }).ToListAsync();

        var maintCount = new Dictionary<int, int>();
        var maintFee = new Dictionary<int, decimal>();
        foreach (var m in maints)
        {
            maintCount[m.VehicleId] = maintCount.GetValueOrDefault(m.VehicleId) + 1;
            if (m.Status == "已完成") maintFee[m.VehicleId] = maintFee.GetValueOrDefault(m.VehicleId) + m.MaintainFee;
        }
        return (maintCount, maintFee);
    }

    ///<summary>把四份内存聚合铺成「每车一行」</summary>
    private async Task<List<VehicleStatRowDto>> BuildVehicleStatRowsAsync(
        (Dictionary<int, int> OrderCount, Dictionary<int, decimal> Hours, Dictionary<int, DateTime> LastRent, Dictionary<int, int> OrderVehicle) usage,
        Dictionary<int, decimal> revenue,
        (Dictionary<int, int> Count, Dictionary<int, decimal> Fee) maint)
    {
        var vehicles = await _db.Vehicles.AsNoTracking().Select(v => new { v.VehicleId, v.VehicleNo, v.Brand, v.VehicleStatus }).ToListAsync();

        return vehicles.Select(v => new VehicleStatRowDto
        {
            VehicleId = v.VehicleId,
            VehicleNo = v.VehicleNo,
            Brand = v.Brand,
            VehicleStatus = v.VehicleStatus,
            OrderCount = usage.OrderCount.GetValueOrDefault(v.VehicleId),
            RentHours = Math.Round(usage.Hours.GetValueOrDefault(v.VehicleId), 1),
            Revenue = Math.Round(revenue.GetValueOrDefault(v.VehicleId), 2),
            MaintenanceCount = maint.Count.GetValueOrDefault(v.VehicleId),
            MaintenanceFee = Math.Round(maint.Fee.GetValueOrDefault(v.VehicleId), 2),
            LastRentTime = usage.LastRent.TryGetValue(v.VehicleId, out var t) ? t.ToString("yyyy-MM-dd HH:mm") : null
        })
            .ToList();
    }

    ///<summary>筛选条件都是「空即不过滤」：状态精确匹配，关键字命中车号或品牌（忽略大小写）</summary>
    private static IEnumerable<VehicleStatRowDto> FilterVehicleStatRows(
        IEnumerable<VehicleStatRowDto> rows, string? keyword, string? status) =>
        rows.Where(r => string.IsNullOrWhiteSpace(status) || r.VehicleStatus == status)
            .Where(r => MatchesVehicleKeyword(r, keyword));

    private static bool MatchesVehicleKeyword(VehicleStatRowDto row, string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return true;
        var k = keyword.Trim();
        return row.VehicleNo.Contains(k, StringComparison.OrdinalIgnoreCase)
            || (row.Brand != null && row.Brand.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    ///<summary>维保费用统计</summary>
    public async Task<MaintenanceStatDto> GetMaintenanceStatsAsync(DateTime? from, DateTime? to)
    {
        var (rangeFrom, rangeTo, hasRange) = ResolveDateRange(from, to);
        var rows = await LoadMaintenanceOrdersAsync(rangeFrom, rangeTo, hasRange);

        //按月在区间内铺满
        var monthly = SeedMonthlyBuckets(hasRange, rangeFrom, rangeTo);

        //总额与月度桶同一遍遍历同时累加，两者必然同源，不会出现「总额」与「折线求和」对不上
        var totalFee = AccumulateMonthlyFee(rows, monthly);

        var faultTypes = BuildFaultTypeStats(rows);
        ApplyFaultTypeShare(faultTypes, totalFee);
        var topVehicles = BuildTopMaintenanceVehicles(rows);

        return new MaintenanceStatDto
        {
            From = rangeFrom?.ToString(DateFormatDay) ?? "",
            To = to?.ToString(DateFormatDay) ?? "",
            TotalCount = rows.Count,
            TotalFee = totalFee,
            MonthlyFee = monthly.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2)),
            FaultTypes = faultTypes,
            TopVehicles = topVehicles
        };
    }

    ///<summary>取区间内的维保工单（带车辆）</summary>
    private async Task<List<MaintenanceOrder>> LoadMaintenanceOrdersAsync(DateTime? rangeFrom, DateTime? rangeTo, bool hasRange)
    {
        var q = _db.MaintenanceOrders.AsNoTracking().AsQueryable();
        if (hasRange) q = q.Where(m => m.CreateTime >= rangeFrom && m.CreateTime < rangeTo);
        return await q.Include(m => m.Vehicle).ToListAsync();
    }

    ///<summary>把区间内每个自然月先放一个 0 桶：折线图要的是连续月份，缺月不能被相邻两点直接连起来</summary>
    private static Dictionary<string, decimal> SeedMonthlyBuckets(bool hasRange, DateTime? rangeFrom, DateTime? rangeTo)
    {
        var monthly = new Dictionary<string, decimal>();
        if (!hasRange) return monthly;

        for (var m = new DateTime(rangeFrom!.Value.Year, rangeFrom.Value.Month, 1, 0, 0, 0, DateTimeKind.Local);
             m < rangeTo!.Value;
             m = m.AddMonths(1))
            monthly[m.ToString(DateFormatMonth)] = 0m;
        return monthly;
    }

    ///<summary>累加已完工工单费用到总额与月度桶，返回四舍五入到分的总额</summary>
    private static decimal AccumulateMonthlyFee(List<MaintenanceOrder> rows, Dictionary<string, decimal> monthly)
    {
        var totalFee = 0m;
        foreach (var r in rows)
        {
            var month = r.CreateTime.ToString(DateFormatMonth);
            monthly.TryAdd(month, 0m);
            if (r.Status != "已完成") continue;
            totalFee += r.MaintainFee;
            monthly[month] += r.MaintainFee;
        }
        return Math.Round(totalFee, 2);
    }

    ///<summary>故障类型分布</summary>
    private static List<FaultTypeStatDto> BuildFaultTypeStats(List<MaintenanceOrder> rows) =>
        rows.GroupBy(r => string.IsNullOrWhiteSpace(r.FaultType) ? "未归类" : r.FaultType)
            .Select(g => new FaultTypeStatDto
            {
                FaultType = g.Key,
                Count = g.Count(),
                Fee = Math.Round(g.Where(x => x.Status == "已完成").Sum(x => x.MaintainFee), 2)
            })
            .OrderByDescending(x => x.Fee).ThenBy(x => x.FaultType)
            .ToList();

    ///<summary>占比=该项已完工费用/全区间已完工费用</summary>
    private static void ApplyFaultTypeShare(List<FaultTypeStatDto> faultTypes, decimal totalFee)
    {
        foreach (var f in faultTypes)
            f.Share = totalFee == 0m ? 0m : Math.Round(f.Fee * 100m / totalFee, 1);
    }

    ///<summary>单台车辆维保费用排行</summary>
    private static List<MaintenanceRankDto> BuildTopMaintenanceVehicles(List<MaintenanceOrder> rows) =>
        rows.GroupBy(r => new { r.VehicleId, VehicleNo = r.Vehicle?.VehicleNo ?? "", Brand = BrandOf(r.Vehicle) })
            .Select(g => new MaintenanceRankDto
            {
                VehicleId = g.Key.VehicleId,
                VehicleNo = g.Key.VehicleNo,
                Brand = g.Key.Brand,
                Count = g.Count(),
                Fee = Math.Round(g.Where(x => x.Status == "已完成").Sum(x => x.MaintainFee), 2)
            })
            .OrderByDescending(x => x.Fee).ThenBy(x => x.VehicleNo)
            .Take(10)
            .ToList();

    ///<summary>取车辆品牌</summary>
    private static string? BrandOf(Vehicle? vehicle) => vehicle?.Brand;

    ///<summary>按天铺满营收/订单趋势：两条 GROUP BY 查询覆盖整个区间</summary>
    private async Task FillDailyTrendAsync(DateTime start, DateTime end, Dictionary<string, decimal> revenue, Dictionary<string, int> orders)
    {
        var revenueRows = await _db.PaymentRecords.Where(p => PayStatuses.MoneyArrived.Contains(p.PayStatus) && p.PayTime.HasValue && p.PayTime >= start && p.PayTime < end).GroupBy(p => p.PayTime!.Value.Date).Select(g => new { Day = g.Key, Total = g.Sum(x => x.PayAmount) }).ToListAsync();

        var orderRows = await _db.RentalOrders.Where(o => o.CreateTime >= start && o.CreateTime < end).GroupBy(o => o.CreateTime.Date).Select(g => new { Day = g.Key, Count = g.Count() }).ToListAsync();

        var revenueByDay = revenueRows.ToDictionary(r => r.Day, r => r.Total);
        var ordersByDay = orderRows.ToDictionary(r => r.Day, r => r.Count);

        for (var d = start.Date; d < end.Date; d = d.AddDays(1))
        {
            var key = d.ToString("MM-dd");
            revenue[key] = revenueByDay.GetValueOrDefault(d, 0m);
            orders[key] = ordersByDay.GetValueOrDefault(d, 0);
        }
    }

    ///<summary>按月铺满趋势（长区间用）</summary>
    private async Task FillMonthlyTrendAsync(DateTime start, DateTime end, Dictionary<string, decimal> revenue, Dictionary<string, int> orders)
    {
        var revenueRows = await _db.PaymentRecords.Where(p => PayStatuses.MoneyArrived.Contains(p.PayStatus) && p.PayTime.HasValue && p.PayTime >= start && p.PayTime < end).GroupBy(p => new { p.PayTime!.Value.Year, p.PayTime.Value.Month }).Select(g => new { g.Key.Year, g.Key.Month, Total = g.Sum(x => x.PayAmount) }).ToListAsync();

        var orderRows = await _db.RentalOrders.Where(o => o.CreateTime >= start && o.CreateTime < end).GroupBy(o => new { o.CreateTime.Year, o.CreateTime.Month }).Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() }).ToListAsync();

        var revenueByMonth = revenueRows.ToDictionary(r => (r.Year, r.Month), r => r.Total);
        var ordersByMonth = orderRows.ToDictionary(r => (r.Year, r.Month), r => r.Count);

        for (var m = new DateTime(start.Year, start.Month, 1, 0, 0, 0, DateTimeKind.Local); m < end; m = m.AddMonths(1))
        {
            var key = m.ToString(DateFormatMonth);
            revenue[key] = revenueByMonth.GetValueOrDefault((m.Year, m.Month), 0m);
            orders[key] = ordersByMonth.GetValueOrDefault((m.Year, m.Month), 0);
        }
    }
}
using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class ReportService(AppDbContext db, MessageService messages)
{
    private readonly AppDbContext _db = db;
    private readonly MessageService _messages = messages;

    /// <summary>统计看板。from/to 为可选时间段：传了只统计区间内，不传统计全部（累计口径）</summary>
    public async Task<VisualReport> GetDashboard(DateTime? from = null, DateTime? to = null)
    {
        var today = DateTime.Now.Date;
        var tomorrow = today.AddDays(1);
        var now = DateTime.Now;
        var soon = now.AddHours(24);

        // 区间右端点取「当日 23:59:59」：日期选择器给的是当天 00:00，
        // 直接用会把截止日当天的数据全漏掉，这是区间统计最常见的错。
        var rangeFrom = from?.Date;
        var rangeTo = to?.Date.AddDays(1);
        var hasRange = rangeFrom.HasValue && rangeTo.HasValue;

        // 落在区间内的支付 / 订单。
        // 营收口径 = 「钱到过账」：结算后退押金会把支付记录改成「部分退款」，
        // 只数「已支付」会把已完结订单整批漏掉（对账差额 28,646.72 的根因），
        // 看板营收会与对账页实收对不上。补缴支付同样是营收，随流水计入。
        var payQ = _db.PaymentRecords.Where(p => PayStatuses.MoneyArrived.Contains(p.PayStatus));
        var orderQ = _db.RentalOrders.AsQueryable();
        if (hasRange)
        {
            payQ = payQ.Where(p => p.PayTime.HasValue && p.PayTime >= rangeFrom && p.PayTime < rangeTo);
            orderQ = orderQ.Where(o => o.CreateTime >= rangeFrom && o.CreateTime < rangeTo);
        }

        // 趋势先填进本地字典，再随对象一次性构造。
        // 报表是不可变快照：构造完就不该再被别的代码回填字段，否则「这张报表到底是谁算的」说不清。
        var monthlyRevenue = new Dictionary<string, decimal>();
        var orderTrend = new Dictionary<string, int>();

        if (hasRange)
        {
            var days = (int)(rangeTo!.Value.Date - rangeFrom!.Value.Date).TotalDays;
            // 区间 ≤31 天按天铺点；更长的区间按月聚合，避免图上出现几百个点
            if (days <= 31)
                await FillDailyTrendAsync(rangeFrom.Value.Date, rangeTo.Value.Date, monthlyRevenue, orderTrend);
            else
                await FillMonthlyTrendAsync(rangeFrom.Value.Date, rangeTo.Value.Date, monthlyRevenue, orderTrend);
        }
        else
        {
            // 无区间：最近 7 天
            await FillDailyTrendAsync(DateTime.Now.AddDays(-6).Date, DateTime.Now.Date.AddDays(1),
                monthlyRevenue, orderTrend);
        }

        // 车辆状态分布：一次全表分组，把「在租 / 空闲 / 维保 / 总数」全部读出来。
        // 既是前端车辆状态饼图的数据源，也避免 vehicles 表为四个存量指标被 COUNT 四遍。
        // 状态分布是「当前时点」的存量指标，不随时间区间变化，保持全表口径。
        var vehicleStatusStats = await _db.Vehicles
            .GroupBy(v => v.VehicleStatus)
            .ToDictionaryAsync(g => g.Key, g => g.Count());

        var draft = new VisualReport
        {
            // ===== 累计指标（指定区间时即为「区间内」指标）=====
            TotalRevenue = await payQ.SumAsync(p => p.PayAmount),
            TotalOrders = await orderQ.CountAsync(),
            TotalCustomers = await _db.Customers.CountAsync(),
            TotalMaintenanceFee = await _db.MaintenanceOrders.Where(m => m.Status == "已完成").SumAsync(m => m.MaintainFee),
            VehicleStatusStats = vehicleStatusStats,
            OrderStatusStats = await orderQ.GroupBy(o => o.OrderStatus).ToDictionaryAsync(g => g.Key, g => g.Count()),

            // ===== 首页看板指标 =====
            TodayRevenue = await _db.PaymentRecords
                .Where(p => PayStatuses.MoneyArrived.Contains(p.PayStatus) && p.PayTime.HasValue && p.PayTime >= today && p.PayTime < tomorrow)
                .SumAsync(p => p.PayAmount),
            TodayOrders = await _db.RentalOrders.CountAsync(o => o.CreateTime >= today && o.CreateTime < tomorrow),
            RentedCount = vehicleStatusStats.GetValueOrDefault("在租"),
            FreeCount = vehicleStatusStats.GetValueOrDefault("空闲"),
            ActiveOrderCount = await _db.RentalOrders.CountAsync(o => o.OrderStatus == "已支付" || o.OrderStatus == "租赁中"),
            OverdueCount = await _db.RentalOrders.CountAsync(o => o.LeaseEnd < now
                && o.OrderStatus != "已完结" && o.OrderStatus != "已取消"),
            ExpiringCount = await _db.RentalOrders.CountAsync(o => o.LeaseEnd >= now && o.LeaseEnd <= soon
                && o.OrderStatus != "已完结" && o.OrderStatus != "已取消"),
            PendingPayCount = await _db.RentalOrders.CountAsync(o => o.OrderStatus == "待支付"),
            MaintenanceCount = vehicleStatusStats.GetValueOrDefault("维保"),
            IncompleteProfileCount = await _db.Customers.CountAsync(c => c.IdCard == null || c.IdCard == "" || c.ContactPhone == null || c.ContactPhone == ""),
            // 保养到期：与工作台预警区、通知列表共用同一段计算，避免两处口径算出两个数
            MaintenanceDueCount = (await GetMaintenanceDueAsync(now)).Count,
            // 营收趋势：无区间给最近 7 天；区间超过 31 天按天铺会出几百个点，改为按月聚合
            MonthlyRevenue = monthlyRevenue,
            OrderTrend = orderTrend
        };

        // 运营指标取报表自身的累计值与订单状态分布来算，避免同一份数据被两套口径各算一遍
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

    /// <summary>
    /// 计算管理员端专属的运营指标。
    ///
    /// 与财务指标的边界：这里只回答「车队转得动不动、订单来不来得及」，
    /// 不涉及金额归属、应收应付、对账差异——那些归财务端。
    /// </summary>
    private static async Task<OperationMetrics> ComputeOperationMetrics(IQueryable<RentalOrder> orderQ, VisualReport report)
    {
        // ⚠️ 必须取回内存再算：DateTime 相减在 EF 里翻译不了，
        // 写进 LINQ 会抛 No coercion operator... 直接 500（本项目踩过）。
        var orders = await orderQ
            .Select(o => new { o.OrderStatus, o.VehicleId, o.LeaseStart, o.LeaseEnd })
            .ToListAsync();

        // 车辆总数直接取报表里的状态分布合计：口径与前端饼图一致，且不再额外查一次库
        var totalVehicleCount = report.VehicleStatusStats?.Values.Sum() ?? 0;

        // 使用率分母恒为「全部车辆」：运营关心的是车队整体周转，不是某个区间的存量
        var rentedVehicleCount = orders.Select(x => x.VehicleId).Distinct().Count();
        var vehicleUsageRate = totalVehicleCount == 0
            ? 0m
            : Math.Round(rentedVehicleCount * 100m / totalVehicleCount, 1);

        // 取消率直接取报表里的订单状态分布，保证与前端柱状图看到的是同一份数
        var statusTotal = report.OrderStatusStats?.Values.Sum() ?? 0;
        var canceled = report.OrderStatusStats?.GetValueOrDefault("已取消") ?? 0;
        var cancelRate = statusTotal == 0
            ? 0m
            : Math.Round(canceled * 100m / statusTotal, 1);

        // LeaseEnd / LeaseStart 均为非空 DateTime（见 Models/RentalOrder.cs），无需判空
        var durations = orders
            .Where(x => x.LeaseEnd > x.LeaseStart)
            .Select(x => (x.LeaseEnd - x.LeaseStart).TotalHours)
            .ToList();
        var avgRentHours = durations.Count == 0
            ? 0m
            : Math.Round((decimal)durations.Average(), 1);

        var revenuePerVehicle = totalVehicleCount == 0
            ? 0m
            : Math.Round(report.TotalRevenue / totalVehicleCount, 2);

        return new OperationMetrics(
            totalVehicleCount, rentedVehicleCount, vehicleUsageRate, avgRentHours, revenuePerVehicle, cancelRate);
    }

    /// <summary>运营指标的中间结果：算完一次性交给报表，不再回头改报表字段</summary>
    private sealed record OperationMetrics(
        int TotalVehicleCount,
        int RentedVehicleCount,
        decimal VehicleUsageRate,
        decimal AvgRentHours,
        decimal RevenuePerVehicle,
        decimal CancelRate);

    // ===== 财务下钻支持的维度 =====
    public const string DimTime = "time";
    public const string DimOrder = "order";
    public const string DimMerchant = "merchant";

    /// <summary>
    /// 财务下钻：按「时间 / 订单 / 商户」三个维度拆开看金额构成与逐笔明细。
    ///
    /// 为什么财务需要它：原来的报表只给到「区间营收 ¥X」这一个合计数，
    /// 财务发现账不平（对账差异 ≠ 0）时无法定位差在哪一天、哪一单、哪个收单渠道。
    /// 下钻把合计数还原成可核对的明细行，对账差异才能落到具体对象上。
    ///
    /// ⚠️ EF Core 铁律（本项目踩过）：DateTime 分组 / DateTime 相减**不能写进 LINQ**，
    /// 会抛 No coercion operator... 直接 500。故统一先 ToListAsync() 取回内存再分组。
    /// </summary>
    /// <param name="from">统计起始日（含）；传 null 表示不限制起点</param>
    /// <param name="to">统计截止日（含当天）；右端点取次日 00:00，避免漏掉截止日当天</param>
    /// <param name="dim">time=按日 / order=按订单 / merchant=按收单渠道</param>
    public async Task<FinanceDrillDto> GetFinanceDrillAsync(DateTime? from, DateTime? to, string? dim)
    {
        var dimension = (dim ?? DimTime).Trim().ToLowerInvariant();
        if (dimension is not (DimTime or DimOrder or DimMerchant)) dimension = DimTime;

        // 区间右端点取「次日 00:00」，否则截止日当天会被整体漏掉
        var rangeFrom = from?.Date;
        var rangeTo = to?.Date.AddDays(1);
        var hasRange = rangeFrom.HasValue && rangeTo.HasValue;

        // 实收口径与对账页严格一致：只数订单支付（biz_type='order'）里「钱到过账」的记录。
        // ① 状态：结算退押金后支付记录变为「部分退款」，只数「已支付」会漏掉已完结订单
        //    （对账差额 28,646.72 的根因），下钻的「实收」卡会与对账卡的「实收」对不上；
        // ② 补缴（biz_type='supplement'）不计入实收——对账页把它单列在「补缴」一行，
        //    混进来两个卡片的数就对不上了（补缴明细在「支付流水」区照常可见）。
        // 注意与对账页的视角差异：这里按支付日期（PayTime）取数，对账页按订单下单日期取数，
        // 跨日收款的订单两屏数字本就不同属同一口径，财务以下钻看每日进账、以对账看订单应收。
        var payQ = _db.PaymentRecords.Where(p => p.BizType == "order"
            && PayStatuses.MoneyArrived.Contains(p.PayStatus) && p.PayTime.HasValue);
        var orderQ = _db.RentalOrders.AsQueryable();
        if (hasRange)
        {
            payQ = payQ.Where(p => p.PayTime >= rangeFrom && p.PayTime < rangeTo);
            orderQ = orderQ.Where(o => o.CreateTime >= rangeFrom && o.CreateTime < rangeTo);
        }

        // 先落内存：后续所有分组/汇总都在内存里做，不再回数据库
        var pays = await payQ.ToListAsync();
        var orders = await orderQ.ToListAsync();
        // 订单维度要显示客户 / 车牌，单独带出（不影响上面两个集合）
        var orderExtras = new Dictionary<int, (string Customer, string Vehicle, string Status)>();
        if (dimension == DimOrder)
        {
            var extra = await orderQ
                .Select(o => new
                {
                    o.OrderId,
                    o.OrderNo,
                    o.OrderStatus,
                    Customer = o.Customer != null ? o.Customer.FullName : "",
                    Vehicle = o.Vehicle != null ? o.Vehicle.VehicleNo : ""
                })
                .ToListAsync();
            foreach (var x in extra)
                orderExtras[x.OrderId] = (x.Customer, x.Vehicle, x.OrderStatus);
        }

        var dto = new FinanceDrillDto
        {
            Dimension = dimension,
            DimensionName = dimension switch
            {
                DimOrder => "按订单",
                DimMerchant => "按商户（收单渠道）",
                _ => "按时间"
            },
            From = from?.Date.ToString("yyyy-MM-dd") ?? "",
            To = to?.Date.ToString("yyyy-MM-dd") ?? "",
            TotalReceived = pays.Sum(p => p.PayAmount),
            TotalRefund = pays.Sum(p => p.RefundAmount),
            PayCount = pays.Count,
            OrderCount = orders.Count,
            TotalRent = orders.Sum(o => o.TotalRent),
            TotalDeposit = orders.Sum(o => o.Deposit)
        };
        dto.NetIncome = Math.Round(dto.TotalReceived - dto.TotalRefund, 2);

        // 订单号 → 该订单的实收 / 退款，供「按订单」与「按时间」两个维度复用。
        // 用 PayAgg 小类而非具名元组：元组元素名经 Dictionary 往返后会退化成
        // (decimal, decimal)，访问 .Received 编译不过（本项目实际踩到）。
        var payByOrder = pays.GroupBy(p => p.OrderId)
            .ToDictionary(g => g.Key, g => new PayAgg
            {
                Received = g.Sum(x => x.PayAmount),
                Refund = g.Sum(x => x.RefundAmount)
            });

        var rows = new List<DrillRowDto>();

        switch (dimension)
        {
            case DimOrder:
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
                dto.Columns = ["订单号", "客户 / 车辆 / 状态", "应收租金", "应收押金", "实收", "退款", "净额"];
                break;

            case DimMerchant:
                // 商户 = 收单渠道（支付宝 / 微信支付 / 银行卡）。
                // 本系统把三条通道各自对应一个收单商户号（pay.prod_pid / sandbox_pid），
                // 故「商户维度」落在 payment_record.pay_type 上。
                var byChannel = pays.GroupBy(p => string.IsNullOrWhiteSpace(p.PayType) ? "未标注" : p.PayType!);
                var totalRecv = dto.TotalReceived;
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
                        Share = totalRecv == 0 ? 0 : Math.Round(recv * 100m / totalRecv, 1)
                    });
                }
                dto.Columns = ["收单渠道", "关联订单", "支付笔数", "实收", "退款", "净额", "占比"];
                break;

            default: // time
                // 区间超过 31 天按天铺会出几百行，改为按月聚合（与趋势图口径保持一致）
                var spanDays = hasRange ? (int)(rangeTo!.Value.Date - rangeFrom!.Value.Date).TotalDays : 7;
                var byMonth = spanDays > 31;
                var buckets = pays
                    .GroupBy(p => byMonth ? p.PayTime!.Value.ToString("yyyy-MM") : p.PayTime!.Value.ToString("MM-dd"))
                    .OrderBy(g => g.Key);
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
                dto.Columns = [byMonth ? "月份" : "日期", "关联订单", "支付笔数", "实收", "退款", "净额"];
                break;
        }

        dto.Rows = rows;
        return dto;
    }

    /// <summary>某笔订单的收款聚合。仅为避免在 Dictionary 里用元组（元组元素名会退化）。</summary>
    private sealed class PayAgg
    {
        public decimal Received { get; init; }
        public decimal Refund { get; init; }
    }

    /// <summary>
    /// 通知中心数据（需求 4.2.4 / 6.1）。
    ///
    /// 先把实时预警幂等落库到通用 message 表（receiver_type='staff'），
    /// 再从 message 表读取该员工的全部消息（含历史已读、以及删除后的结果），
    /// 已读状态直接落在消息行，与顾客消息共用同一张表、同一套已读语义。
    /// </summary>
    public async Task<List<NotificationDto>> GetNotifications(int userId)
    {
        // ① 实时预警 → 幂等 upsert 进通用消息表（同一条业务预警对同一员工只保留一行）
        var alerts = await BuildNotifications();
        var liveGlobal = new HashSet<string>();
        foreach (var a in alerts)
        {
            var (refType, refId) = SplitAlertId(a.Id);
            // 本轮真正触发的全局预警（维保中 / 资料待完善 / 保养到期），供下一步清理旧行用
            if (GlobalAlertTypes.Contains(refType)) liveGlobal.Add(refType);
            // keepReadStateOnChange: 实时预警的标题含「已逾期 N 小时」这类会自己走的时长，
            // 不能让文案一变就把员工刚读过的通知重新标成未读。
            await _messages.SendAsync("staff", userId, refType, refId,
                a.Title, a.Description, a.Level, a.Link, keepReadStateOnChange: true);
        }

        // ② 清掉已经失效的实时预警行。
        //    实时预警是「当前状态」的快照，不是业务流水：订单完结、客户付清、车辆出维保之后，
        //    对应的行如果一直留着，面板只会越堆越多（用户感受就是「消息源源不断」），
        //    还会出现「同一订单既显示即将到期、又显示已逾期」这种自相矛盾的残留。
        //    这里只动 staff 的实时预警类型，顾客的退款/补缴消息一律不碰。
        var liveKeys = await AlertLiveKeysAsync(liveGlobal);

        var realtimeRows = await _db.Messages
            .Where(m => m.ReceiverType == "staff" && m.ReceiverId == userId
                        && RealtimeAlertTypes.Contains(m.RefType!))
            .ToListAsync();

        var dead = realtimeRows.Where(m => !liveKeys.Contains(AlertKey(m.RefType, m.RefId))).ToList();

        if (dead.Count > 0)
        {
            _db.Messages.RemoveRange(dead);
            await _db.SaveChangesAsync();
        }

        // ③ 从 message 表读取（含历史已读状态），按时间倒序。
        //    已忽略（软删）的行不返回——员工「删除」通知走 dismissed_at，原因见 MessageService.DismissAsync。
        var rows = await _db.Messages.AsNoTracking()
            .Where(m => m.ReceiverType == "staff" && m.ReceiverId == userId && m.DismissedAt == null)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();

        return rows.Select(m => new NotificationDto
        {
            MessageId = m.MessageId,
            Id = string.IsNullOrEmpty(m.RefId) ? m.RefType : $"{m.RefType}-{m.RefId}",
            Level = m.Level ?? "info",
            Category = CategoryOf(m.RefType),
            Title = m.Title,
            Description = m.Content,
            Time = m.CreatedAt,
            Link = m.Link,
            IsRead = m.IsRead,
            ReadTime = m.ReadAt
        }).ToList();
    }

    /// <summary>全局聚合类预警的 ref_id 固定值（不能为空：唯一索引里 NULL 互不相等，会反复插新行）。</summary>
    private const string GlobalRefId = "global";

    /// <summary>message 行业务键：与前端拿到的 Id（ref_type-ref_id）保持同一套拼法。</summary>
    private static string AlertKey(string? refType, string? refId) => $"{refType}-{refId ?? string.Empty}";

    /// <summary>
    /// 本轮「应当存在」的实时预警键集合。
    ///
    /// 单独查一次，而不是复用 <see cref="BuildNotifications"/> 的结果：后者为了界面性能做了 Take(20)，
    /// 拿它当清理依据会把第 21 条之后的预警每轮删掉再重建（未读状态来回跳）。
    /// </summary>
    private async Task<HashSet<string>> AlertLiveKeysAsync(HashSet<string> liveGlobal)
    {
        var now = DateTime.Now;
        var soon = now.AddHours(24);
        var live = new HashSet<string>();

        var orders = await _db.RentalOrders.AsNoTracking()
            .Where(o => o.OrderStatus != "已完结" && o.OrderStatus != "已取消")
            .Select(o => new { o.OrderId, o.LeaseEnd, o.OrderStatus })
            .ToListAsync();

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

    private static readonly HashSet<string> GlobalAlertTypes = ["maintenance", "maintain_due", "profile_incomplete"];

    /// <summary>由实时状态推导的预警类型：都随业务状态变化而失效，需要按轮次清理。</summary>
    private static readonly HashSet<string> RealtimeAlertTypes =
        ["overdue", "expiring", "pending", "maintenance", "maintain_due", "profile_incomplete"];

    /// <summary>
    /// 标记已读：ids 为消息主键（message_id）列表；为空表示「全部标记已读」。
    /// 直接更新 message 表中的 is_read / read_at，按 (staff, userId) 限定归属。
    /// </summary>
    public async Task MarkRead(int userId, List<int>? ids)
    {
        var q = _db.Messages.Where(m => m.ReceiverType == "staff"
                                       && m.ReceiverId == userId
                                       && !m.IsRead);
        if (ids != null && ids.Count > 0)
            q = q.Where(m => ids.Contains(m.MessageId));

        var now = DateTime.Now;
        var targets = await q.ToListAsync();
        foreach (var m in targets)
        {
            m.MarkRead(now);
        }
        if (targets.Count > 0) await _db.SaveChangesAsync();
    }

    /// <summary>
    /// 删除（忽略）通知：ids 为空表示「忽略全部」。返回实际忽略条数。
    /// 员工预警由实时状态推导、每次查询都幂等重建，硬删会被下一次拉取复活，
    /// 因此软删（dismissed_at）；读取端过滤已忽略行，预警失效时行被硬删、标记清除。
    /// </summary>
    public async Task<int> Dismiss(int userId, List<int>? ids) =>
        await _messages.DismissAsync("staff", userId, ids);

    /// <summary>把预警的稳定字符串 ID（overdue-12 / maintain-due 等）拆成 (ref_type, ref_id)。</summary>
    private static (string RefType, string RefId) SplitAlertId(string id) => id switch
    {
        /*
         * 全局聚合类预警（维保中车辆数 / 待完善资料数 / 保养到期数）没有业务实体，
         * 归一到固定的 ref_id="global"。
         *
         * 这里不能像以前那样给空串：SendAsync 会把空串还原成 NULL，而 uk_msg_biz
         * 的唯一索引里 NULL 彼此互不相等 —— MySQL 认为 (staff,1,maintenance,NULL) 每条都不一样，
         * ON DUPLICATE KEY 永远不命中，于是每轮 60 秒轮询都插一条新消息，
         * 表现就是铃铛未读数一路狂涨、列表里全是「有 N 台车辆处于维保中」。
         */
        "maintenance" => ("maintenance", GlobalRefId),
        "profile-incomplete" => ("profile_incomplete", GlobalRefId),
        "maintain-due" => ("maintain_due", GlobalRefId),
        _ => SplitPrefixedId(id)
    };

    private static (string RefType, string RefId) SplitPrefixedId(string id)
    {
        var i = id.IndexOf('-');
        return i > 0 ? (id[..i], id[(i + 1)..]) : (id, "");
    }

    /// <summary>ref_type → 前端展示用的分类名（message 表不重复存中文分类）。</summary>
    private static string CategoryOf(string refType) => refType switch
    {
        "overdue" => "逾期预警",
        "expiring" => "到期提醒",
        "pending" => "待支付",
        "maintenance" => "维保提醒",
        "profile_incomplete" => "资料待完善",
        "maintain_due" => "保养提醒",
        "refund" => "退款通知",
        "supplement" => "补缴通知",
        "supplement_paid" => "补缴成功",
        _ => "通知"
    };

    /// <summary>实时计算通知条目（不含已读状态，已读由 GetNotifications 合并）</summary>
    private async Task<List<NotificationDto>> BuildNotifications()
    {
        var list = new List<NotificationDto>();
        var now = DateTime.Now;

        // 1. 逾期未归还
        var overdue = await _db.RentalOrders.Include(o => o.Customer).Include(o => o.Vehicle)
            .Where(o => o.LeaseEnd < now && o.OrderStatus != "已完结" && o.OrderStatus != "已取消")
            .OrderBy(o => o.LeaseEnd).Take(20).ToListAsync();
        foreach (var o in overdue)
        {
            /*
             * 逾期时长必须按「实际过了多久」写，不能一律按天取整。
             *
             * 原来是 Math.Max(1, (int)(now - LeaseEnd).TotalDays)：
             *   · 刚过期 44 分钟 → TotalDays = 0.03 → (int) 截断成 0 → Max(1, 0) 又抬成 1，
             *     于是「到期 15:35、通知 16:19」也能写出「已逾期 1 天」，与实际差了 30 多倍；
             *   · 反过来逾期 1 天 23 小时也只显示「1 天」，把快到惩罚档的账写成刚过期。
             * 现在按「天 + 小时」分段：不足 1 小时报分钟，不足 1 天报小时，满 1 天带零头小时。
             */
            var span = now - o.LeaseEnd;
            var fullDays = (int)span.TotalDays;            // 满 24 小时才算一天
            var restHours = span.Hours;                    // 不足一天的零头
            var overdueText = fullDays > 0
                ? (restHours > 0 ? $"{fullDays} 天 {restHours} 小时" : $"{fullDays} 天")
                : span.TotalHours >= 1
                    ? $"{(int)span.TotalHours} 小时"
                    : $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} 分钟";
            list.Add(new NotificationDto
            {
                Id = $"overdue-{o.OrderId}",
                Level = "danger",
                Category = "逾期预警",
                Title = $"订单 {o.OrderNo} 已逾期 {overdueText}",
                Description = $"客户：{o.Customer?.FullName}　车辆：{o.Vehicle?.VehicleNo}　到期：{o.LeaseEnd:MM-dd HH:mm}，请尽快联系客户跟进",
                Time = o.LeaseEnd,
                Link = "/orders"
            });
        }

        // 2. 24 小时内到期
        var soon = now.AddHours(24);
        var expiring = await _db.RentalOrders.Include(o => o.Customer).Include(o => o.Vehicle)
            .Where(o => o.LeaseEnd >= now && o.LeaseEnd <= soon && o.OrderStatus != "已完结" && o.OrderStatus != "已取消")
            .OrderBy(o => o.LeaseEnd).Take(20).ToListAsync();
        foreach (var o in expiring)
        {
            list.Add(new NotificationDto
            {
                Id = $"expiring-{o.OrderId}",
                Level = "warning",
                Category = "到期提醒",
                Title = $"订单 {o.OrderNo} 即将到期",
                Description = $"客户：{o.Customer?.FullName}　车辆：{o.Vehicle?.VehicleNo}　到期：{o.LeaseEnd:MM-dd HH:mm}",
                Time = o.LeaseEnd,
                Link = "/orders"
            });
        }

        // 3. 待支付订单
        var pending = await _db.RentalOrders.Include(o => o.Customer)
            .Where(o => o.OrderStatus == "待支付").OrderByDescending(o => o.CreateTime).Take(20).ToListAsync();
        foreach (var o in pending)
        {
            list.Add(new NotificationDto
            {
                Id = $"pending-{o.OrderId}",
                Level = "info",
                Category = "待支付",
                Title = $"订单 {o.OrderNo} 待支付",
                Description = $"客户：{o.Customer?.FullName}　金额：¥{o.TotalRent + o.Deposit:F2}",
                Time = o.CreateTime,
                Link = "/orders"
            });
        }

        // 4. 维保中车辆
        var maintaining = await _db.Vehicles.Where(v => v.VehicleStatus == "维保").Take(20).ToListAsync();
        if (maintaining.Count > 0)
        {
            list.Add(new NotificationDto
            {
                Id = "maintenance",
                Level = "warning",
                Category = "维保提醒",
                Title = $"有 {maintaining.Count} 台车辆处于维保中",
                Description = string.Join("、", maintaining.Select(v => v.VehicleNo)),
                Time = now,
                Link = "/maintenance"
            });
        }

        // 5. 客户资料待完善（身份证号 / 手机号未采集）
        var incomplete = await _db.Customers
            .Where(c => c.IdCard == null || c.IdCard == "" || c.ContactPhone == null || c.ContactPhone == "")
            .Take(20).ToListAsync();
        if (incomplete.Count > 0)
        {
            list.Add(new NotificationDto
            {
                Id = "profile-incomplete",
                Level = "info",
                Category = "资料待完善",
                Title = $"有 {incomplete.Count} 位客户待完善租车资料",
                Description = "注册时仅采集用户名与密码，租车下单前需补录身份证号与手机号",
                Time = now,
                Link = "/customers"
            });
        }

        // 6. 保养提醒（需求 4.2.6）：周期取 maintain_cycle_days，以「最近一次已完成维保」为基准，
        //    从未维保过的以购入时间为基准；配置为 0 表示关闭。
        //    计算与工作台预警卡的「保养到期」计数共用 GetMaintenanceDueAsync，两处永不打架。
        var maintainCycleDays = ConfigValue.Int(await _db.SystemConfigs.ToListAsync(), "maintain_cycle_days", 90);
        if (maintainCycleDays > 0)
        {
            var due = await GetMaintenanceDueAsync(now);
            if (due.Count > 0)
            {
                list.Add(new NotificationDto
                {
                    Id = "maintain-due",
                    Level = "warning",
                    Category = "保养提醒",
                    Title = $"有 {due.Count} 台车辆已超过 {maintainCycleDays} 天保养周期",
                    Description = string.Join("、", due.Take(10).Select(d => $"{d.VehicleNo}（已 {d.Days} 天）"))
                        + (due.Count > 10 ? $" 等 {due.Count} 台" : ""),
                    Time = now,
                    Link = "/maintenance"
                });
            }
        }

        return [.. list.OrderByDescending(n => n.Time)];
    }

    /// <summary>
    /// 保养到期车辆清单（需求 4.2.6）。口径：以每台车「最近一次已完成维保」为基准，
    /// 从未维保过的以购入时间为基准，达到 maintain_cycle_days（默认 90，设为 0 关闭）即列入。
    /// </summary>
    /// <remarks>
    /// 工作台预警卡要的是「有几台」，通知列表要的是「哪几台」——同一套判定逻辑抽在这里，
    /// 两处调用同一个方法，避免卡片上是 3 台、点进列表只剩 2 台的打脸情况。
    /// 报废车辆不参与保养巡检。
    /// </remarks>
    private async Task<List<(string VehicleNo, int Days)>> GetMaintenanceDueAsync(DateTime now)
    {
        var cycle = ConfigValue.Int(await _db.SystemConfigs.ToListAsync(), "maintain_cycle_days", 90);
        if (cycle <= 0) return [];

        var alive = await _db.Vehicles.AsNoTracking().Where(v => v.VehicleStatus != "报废").ToListAsync();
        var lastFinish = await _db.MaintenanceOrders.AsNoTracking()
            .Where(m => m.FinishTime != null)
            .GroupBy(m => m.VehicleId)
            .Select(g => new { VehicleId = g.Key, Last = g.Max(m => m.FinishTime) })
            .ToListAsync();
        var lastMap = lastFinish.ToDictionary(x => x.VehicleId, x => x.Last);

        var due = new List<(string, int)>();
        foreach (var v in alive)
        {
            var basis = lastMap.TryGetValue(v.VehicleId, out var t) && t.HasValue ? t.Value : v.BuyTime;
            if (basis == null) continue;
            var days = (int)(now - basis.Value).TotalDays;
            if (days >= cycle) due.Add((v.VehicleNo, days));
        }
        return due;
    }

    // ===== 车辆运营统计 / 维保费用统计（需求 4.2.7）=====

    /// <summary>
    /// 车辆运营统计（需求 4.2.7）：每台车的累计租赁时长、累计营收、维保次数与维保费用，
    /// 用来识别高价值车辆与闲置车辆。支持按车辆编号 / 品牌关键字与车辆状态筛选。
    ///
    /// 与报表其余部分同一条铁律：DateTime 相减在 EF 里翻译不了，必须先取回内存再算。
    /// </summary>
    /// <param name="from">统计起始日（含）；不传表示不限制起点（累计口径）</param>
    /// <param name="to">统计截止日（含当天）；右端点取次日 00:00，避免漏掉截止日当天</param>
    /// <param name="keyword">车辆编号 / 品牌模糊匹配；为空表示不限</param>
    /// <param name="status">车辆状态精确匹配（空闲 / 在租 / 维保 / 报废）；为空表示不限</param>
    public async Task<VehicleStatDto> GetVehicleStatsAsync(DateTime? from, DateTime? to, string? keyword, string? status)
    {
        var rangeFrom = from?.Date;
        var rangeTo = to?.Date.AddDays(1);
        var hasRange = rangeFrom.HasValue && rangeTo.HasValue;

        var orderQ = _db.RentalOrders.AsNoTracking().AsQueryable();
        if (hasRange) orderQ = orderQ.Where(o => o.CreateTime >= rangeFrom && o.CreateTime < rangeTo);
        var orders = await orderQ
            .Select(o => new { o.OrderId, o.VehicleId, o.LeaseStart, o.LeaseEnd })
            .ToListAsync();

        var payQ = _db.PaymentRecords.AsNoTracking().Where(p => PayStatuses.MoneyArrived.Contains(p.PayStatus) && p.PayTime.HasValue);
        if (hasRange) payQ = payQ.Where(p => p.PayTime >= rangeFrom && p.PayTime < rangeTo);
        var pays = await payQ.Select(p => new { p.OrderId, p.PayAmount }).ToListAsync();

        var maintQ = _db.MaintenanceOrders.AsNoTracking().AsQueryable();
        if (hasRange) maintQ = maintQ.Where(m => m.CreateTime >= rangeFrom && m.CreateTime < rangeTo);
        var maints = await maintQ.Select(m => new { m.VehicleId, m.Status, m.MaintainFee }).ToListAsync();

        var vehicles = await _db.Vehicles.AsNoTracking()
            .Select(v => new { v.VehicleId, v.VehicleNo, v.Brand, v.VehicleStatus })
            .ToListAsync();

        // 营收按「订单所租车辆」归属：一笔流水挂的是订单，车辆要经订单一跳才能对上
        var orderVehicle = orders.ToDictionary(o => o.OrderId, o => o.VehicleId);

        var orderCount = new Dictionary<int, int>();
        var hours = new Dictionary<int, decimal>();
        var lastRent = new Dictionary<int, DateTime>();
        foreach (var o in orders)
        {
            orderCount[o.VehicleId] = orderCount.GetValueOrDefault(o.VehicleId) + 1;
            if (o.LeaseEnd > o.LeaseStart)
                hours[o.VehicleId] = hours.GetValueOrDefault(o.VehicleId) + (decimal)(o.LeaseEnd - o.LeaseStart).TotalHours;
            if (!lastRent.TryGetValue(o.VehicleId, out var t) || o.LeaseStart > t) lastRent[o.VehicleId] = o.LeaseStart;
        }

        var revenue = new Dictionary<int, decimal>();
        foreach (var p in pays)
        {
            if (orderVehicle.TryGetValue(p.OrderId, out var vid))
                revenue[vid] = revenue.GetValueOrDefault(vid) + p.PayAmount;
        }

        var maintCount = new Dictionary<int, int>();
        var maintFee = new Dictionary<int, decimal>();
        foreach (var m in maints)
        {
            maintCount[m.VehicleId] = maintCount.GetValueOrDefault(m.VehicleId) + 1;
            if (m.Status == "已完成")
                maintFee[m.VehicleId] = maintFee.GetValueOrDefault(m.VehicleId) + m.MaintainFee;
        }

        var rows = vehicles
            .Select(v => new VehicleStatRowDto
            {
                VehicleId = v.VehicleId,
                VehicleNo = v.VehicleNo,
                Brand = v.Brand,
                VehicleStatus = v.VehicleStatus,
                OrderCount = orderCount.GetValueOrDefault(v.VehicleId),
                RentHours = Math.Round(hours.GetValueOrDefault(v.VehicleId), 1),
                Revenue = Math.Round(revenue.GetValueOrDefault(v.VehicleId), 2),
                MaintenanceCount = maintCount.GetValueOrDefault(v.VehicleId),
                MaintenanceFee = Math.Round(maintFee.GetValueOrDefault(v.VehicleId), 2),
                LastRentTime = lastRent.TryGetValue(v.VehicleId, out var t) ? t.ToString("yyyy-MM-dd HH:mm") : null
            })
            .Where(r => string.IsNullOrWhiteSpace(status) || r.VehicleStatus == status)
            .Where(r =>
            {
                if (string.IsNullOrWhiteSpace(keyword)) return true;
                var k = keyword.Trim();
                return r.VehicleNo.Contains(k, StringComparison.OrdinalIgnoreCase)
                    || (r.Brand != null && r.Brand.Contains(k, StringComparison.OrdinalIgnoreCase));
            })
            // 营收高的排前面：运营最关心的「哪台车最能赚」一眼可见，闲置车自然沉到最后
            .OrderByDescending(r => r.Revenue).ThenBy(r => r.VehicleNo)
            .ToList();

        return new VehicleStatDto
        {
            From = rangeFrom?.ToString("yyyy-MM-dd") ?? "",
            To = to?.ToString("yyyy-MM-dd") ?? "",
            TotalVehicles = rows.Count,
            IdleVehicles = rows.Count(r => r.OrderCount == 0),
            TotalRentHours = Math.Round(rows.Sum(r => r.RentHours), 1),
            TotalRevenue = Math.Round(rows.Sum(r => r.Revenue), 2),
            TotalMaintenanceCount = rows.Sum(r => r.MaintenanceCount),
            TotalMaintenanceFee = Math.Round(rows.Sum(r => r.MaintenanceFee), 2),
            Rows = rows
        };
    }

    /// <summary>
    /// 维保费用统计（需求 4.2.7）：月度维保总费用、故障类型费用分布、单台车辆维保费用排行。
    ///
    /// 费用一律只统计「已完成」工单——未完工工单的费用还没定，先计入会让月度趋势先虚高再回落；
    /// 工单条数则按全部状态计，因为「这类毛病正在发生」本身就是要看的信号。
    /// </summary>
    /// <param name="from">统计起始日（含）；不传表示不限制起点</param>
    /// <param name="to">统计截止日（含当天）；右端点取次日 00:00</param>
    public async Task<MaintenanceStatDto> GetMaintenanceStatsAsync(DateTime? from, DateTime? to)
    {
        var rangeFrom = from?.Date;
        var rangeTo = to?.Date.AddDays(1);
        var hasRange = rangeFrom.HasValue && rangeTo.HasValue;

        var q = _db.MaintenanceOrders.AsNoTracking().AsQueryable();
        if (hasRange) q = q.Where(m => m.CreateTime >= rangeFrom && m.CreateTime < rangeTo);

        var rows = await q
            .Select(m => new
            {
                m.VehicleId,
                VehicleNo = m.Vehicle != null ? m.Vehicle.VehicleNo : "",
                Brand = m.Vehicle != null ? m.Vehicle.Brand : null,
                m.FaultType,
                m.Status,
                m.MaintainFee,
                m.CreateTime
            })
            .ToListAsync();

        var monthly = new Dictionary<string, decimal>();

        // 按月在区间内铺满：没有工单的月份也要留 0，否则折线图会把断月直接连起来误导读者
        if (hasRange)
        {
            for (var m = new DateTime(rangeFrom!.Value.Year, rangeFrom.Value.Month, 1);
                 m < rangeTo!.Value;
                 m = m.AddMonths(1))
                monthly[m.ToString("yyyy-MM")] = 0m;
        }

        var totalFee = 0m;
        foreach (var r in rows)
        {
            var month = r.CreateTime.ToString("yyyy-MM");
            monthly.TryAdd(month, 0m);
            if (r.Status != "已完成") continue;
            totalFee += r.MaintainFee;
            monthly[month] += r.MaintainFee;
        }

        var faultTypes = rows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.FaultType) ? "未归类" : r.FaultType!)
            .Select(g => new FaultTypeStatDto
            {
                FaultType = g.Key,
                Count = g.Count(),
                Fee = Math.Round(g.Where(x => x.Status == "已完成").Sum(x => x.MaintainFee), 2)
            })
            .OrderByDescending(x => x.Fee).ThenBy(x => x.FaultType)
            .ToList();

        totalFee = Math.Round(totalFee, 2);
        foreach (var f in faultTypes)
            f.Share = totalFee == 0m ? 0m : Math.Round(f.Fee * 100m / totalFee, 1);

        var topVehicles = rows
            .GroupBy(r => new { r.VehicleId, r.VehicleNo, r.Brand })
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

        return new MaintenanceStatDto
        {
            From = rangeFrom?.ToString("yyyy-MM-dd") ?? "",
            To = to?.ToString("yyyy-MM-dd") ?? "",
            TotalCount = rows.Count,
            TotalFee = totalFee,
            MonthlyFee = monthly
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2)),
            FaultTypes = faultTypes,
            TopVehicles = topVehicles
        };
    }

    /// <summary>
    /// 按天铺满营收 / 订单趋势：**两条 GROUP BY 查询**覆盖整个区间，
    /// 而不是「循环每一天各查一次」（31 天区间原本要 62 条 SQL）。
    /// 聚合在库里完成，返回的日期集合再与区间对齐补 0——没有数据的日期仍要有点，否则折线会把缺口连起来。
    /// </summary>
    private async Task FillDailyTrendAsync(
        DateTime start, DateTime end,
        Dictionary<string, decimal> revenue, Dictionary<string, int> orders)
    {
        var revenueRows = await _db.PaymentRecords
            .Where(p => PayStatuses.MoneyArrived.Contains(p.PayStatus) && p.PayTime.HasValue && p.PayTime >= start && p.PayTime < end)
            .GroupBy(p => p.PayTime!.Value.Date)
            .Select(g => new { Day = g.Key, Total = g.Sum(x => x.PayAmount) })
            .ToListAsync();

        var orderRows = await _db.RentalOrders
            .Where(o => o.CreateTime >= start && o.CreateTime < end)
            .GroupBy(o => o.CreateTime.Date)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToListAsync();

        var revenueByDay = revenueRows.ToDictionary(r => r.Day, r => r.Total);
        var ordersByDay = orderRows.ToDictionary(r => r.Day, r => r.Count);

        for (var d = start.Date; d < end.Date; d = d.AddDays(1))
        {
            var key = d.ToString("MM-dd");
            revenue[key] = revenueByDay.GetValueOrDefault(d, 0m);
            orders[key] = ordersByDay.GetValueOrDefault(d, 0);
        }
    }

    /// <summary>
    /// 按月铺满趋势（长区间用）：同样只用两条 GROUP BY，按 (年, 月) 分组后在内存里对齐到月份序列。
    /// </summary>
    private async Task FillMonthlyTrendAsync(
        DateTime start, DateTime end,
        Dictionary<string, decimal> revenue, Dictionary<string, int> orders)
    {
        var revenueRows = await _db.PaymentRecords
            .Where(p => PayStatuses.MoneyArrived.Contains(p.PayStatus) && p.PayTime.HasValue && p.PayTime >= start && p.PayTime < end)
            .GroupBy(p => new { p.PayTime!.Value.Year, p.PayTime.Value.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Total = g.Sum(x => x.PayAmount) })
            .ToListAsync();

        var orderRows = await _db.RentalOrders
            .Where(o => o.CreateTime >= start && o.CreateTime < end)
            .GroupBy(o => new { o.CreateTime.Year, o.CreateTime.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() })
            .ToListAsync();

        var revenueByMonth = revenueRows.ToDictionary(r => (r.Year, r.Month), r => r.Total);
        var ordersByMonth = orderRows.ToDictionary(r => (r.Year, r.Month), r => r.Count);

        for (var m = new DateTime(start.Year, start.Month, 1); m < end; m = m.AddMonths(1))
        {
            var key = m.ToString("yyyy-MM");
            revenue[key] = revenueByMonth.GetValueOrDefault((m.Year, m.Month), 0m);
            orders[key] = ordersByMonth.GetValueOrDefault((m.Year, m.Month), 0);
        }
    }
}
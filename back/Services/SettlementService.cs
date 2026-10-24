using System.Data;
using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class SettlementService(AppDbContext db, PaymentService payment, MessageService messages, ILogger<SettlementService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly PaymentService _payment = payment;
    private readonly MessageService _messages = messages;
    private readonly ILogger<SettlementService> _logger = logger;

    public async Task<List<SettleRecord>> GetAll() =>
        await _db.SettleRecords.Include(s => s.RentalOrder).ThenInclude(o => o!.Customer)
            .OrderByDescending(s => s.SettleTime).ToListAsync();

    /// <summary>
    /// 结算核算结果。
    /// `ActualRent` = 约定租期内的租金，`OverdueFee` = 超出约定还车时间那一段的租金，
    /// 两者**都是租金**（OverdueFee 只是单价上浮的特殊形式），`RentTotal` 是这笔订单的租金合计。
    /// </summary>
    private sealed record SettleCalc(
        decimal HourlyRent, decimal Multiplier,
        int ActualHours, decimal ActualRent,
        int OverdueHours, decimal OverdueFee, decimal RentTotal,
        decimal RentDiff, decimal Refund, decimal Supplement);

    /// <summary>
    /// 统一的结算核算：正常租金只算到约定还车时间，超出部分按超时倍数计为逾期费。
    /// `damageFee` 由调用方给出——试算时按 0（损坏费要前台现场录入），结算时用表单值。
    /// </summary>
    private async Task<SettleCalc> CalculateAsync(RentalOrder order, decimal damageFee)
    {
        var now = DateTime.Now;
        // 计费粒度与下单一致：**按小时**（向上取整，最少 1 小时）。
        // 与实际租金口径一致：**按订单所租车辆的租金**结算（车辆未单独定价才回退全局配置）。
        // 不能图省事只读全局配置，否则会出现「下单按 50/天收、结算按 30/天算」的对账不一致。
        var (_, hourlyRent, _) = await PricingResolver.ResolveHourlyAsync(_db, order.VehicleId);

        // 正常租金只算到约定还车时间（leaseEnd）为止；超出 leaseEnd 的部分属于超时，
        // 单独按「小时租金 × overdue_multiplier」计逾期费，不再由前台手填。
        var normalEnd = order.LeaseEnd < now ? order.LeaseEnd : now;
        var actualHours = PricingResolver.CalcHours(order.LeaseStart, normalEnd);
        var actualRent = Math.Round(actualHours * hourlyRent, 2);

        // 未逾期时这里就是 0：**「不收」也必须有金额**，不能只靠一句规则说明带过。
        var overdueHours = order.LeaseEnd < now ? PricingResolver.CalcHours(order.LeaseEnd, now) : 0;
        var multiplierCfgs = await _db.SystemConfigs
            .Where(c => c.ConfigKey == "overdue_multiplier").ToListAsync();
        var multiplier = ConfigValue.Decimal(multiplierCfgs, "overdue_multiplier", 1.5m);
        var overdueFee = Math.Round(overdueHours * hourlyRent * multiplier, 2);

        // 落库仍拆成 actual_rent / overdue_fee 两列，是为了保留可审计的构成；
        // 合计与多退少补的算法收在 SettlementMath，纯函数、可被单元测试锁住口径。
        var (rentTotal, rentDiff, refund, supplement) = SettlementMath.Settle(
            order.TotalRent, order.Deposit, damageFee, actualRent, overdueFee);

        return new SettleCalc(
            hourlyRent, multiplier, actualHours, actualRent,
            overdueHours, overdueFee, rentTotal, rentDiff,
            refund, supplement);
    }

    /// <summary>
    /// 结算试算（只读，不落库）。前台在结算弹窗选定订单后，需要**当场看到逾期费金额**：
    /// 未逾期显示 ¥0.00，已逾期显示自动计收的数额与算式。
    /// 与 <see cref="Settle"/> 共用 <see cref="CalculateAsync"/>，口径必然一致。
    /// </summary>
    public async Task<SettlePreviewDto> Preview(int orderId)
    {
        var order = await _db.RentalOrders
            .Include(o => o.Customer).Include(o => o.Vehicle)
            .FirstOrDefaultAsync(o => o.OrderId == orderId)
            ?? throw new Exception("订单不存在");

        var c = await CalculateAsync(order, 0m);
        return new SettlePreviewDto
        {
            OrderId = order.OrderId,
            OrderNo = order.OrderNo,
            CustomerName = order.Customer?.FullName ?? "",
            VehicleNo = order.Vehicle?.VehicleNo ?? "",
            LeaseStart = order.LeaseStart,
            LeaseEnd = order.LeaseEnd,
            PrepaidRent = order.TotalRent,
            Deposit = order.Deposit,
            HourlyRent = c.HourlyRent,
            Multiplier = c.Multiplier,
            IsOverdue = c.OverdueHours > 0,
            OverdueHours = c.OverdueHours,
            OverdueFee = c.OverdueFee,
            ActualRent = c.ActualRent,
            RentTotal = c.RentTotal,
            ActualHours = c.ActualHours,
            RentDiff = c.RentDiff,
            RefundAmount = c.Refund,
            SupplementAmount = c.Supplement
        };
    }

    /// <summary>结算结果：单据 + 给前台的提示（尤其是退款是否真的原路退回）</summary>
    public sealed record SettleResult(SettleRecord Record, string Message);

    public async Task<SettleResult> Settle(SettleOrderDto dto, int operatorId)
    {
        // 结算会碰三样「不能重复发生」的东西：结算单、押金退款、车辆释放。
        // 两个前台同时点「结算」时，原实现都能通过状态判定，于是各生成一条结算单、各退一次押金。
        // 这里用「订单行锁 + 唯一索引 + 死锁重试」三层来封死：
        //   行锁让第二个请求排队，拿到锁后重读订单，状态已变「已完结」→ 直接拒绝；
        //   uk_settle_order 兜住极端情况下漏过的并发；
        //   真撞上死锁/等锁超时就整段重放（事务已回滚，重放安全）。
        return await DbConcurrency.RunAsync(
            () => SettleOnce(dto, operatorId), _logger, "订单结算",
            reset: () => _db.ChangeTracker.Clear());
    }

    private async Task<SettleResult> SettleOnce(SettleOrderDto dto, int operatorId)
    {
        if (dto.DamageFee < 0)
            throw new Exception("损坏费不能为负数");

        // READ COMMITTED：结算事务里只锁「自己读到的那一行」。
        // 默认的 REPEATABLE READ 会对扫描到的区间加间隙锁，结算与下单同时在跑时极易互相咬死。
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            // 等锁上限压到 5 秒（MySQL 默认 50 秒会让请求线程与连接池一起被拖住）
            await DbRowLock.PrepareAsync(_db);

            // 锁内重读：这一句之后的 order 才是「当前真实状态」，事务外读到的都可能已过期
            var order = await DbRowLock.LockOrderAsync(_db, dto.OrderId)
                        ?? throw new Exception("订单不存在");
            if (order.OrderStatus is not ("已支付" or "租赁中"))
                throw new Exception($"订单当前状态为「{order.OrderStatus}」，不可结算" +
                                    "（可能已被其他同事结算或取消，请刷新后查看）");

            var c = await CalculateAsync(order, dto.DamageFee);
            var now = DateTime.Now;

            // 本单成功支付的那条记录——退还押金/预付租金必须回到它上面，
            // 否则会出现「结算单写着退了 ¥80，payment_record 一分没退」的对账黑洞。
            var paid = await _db.PaymentRecords
                .Where(p => p.OrderId == order.OrderId && (p.PayStatus == "已支付" || p.PayStatus == "部分退款"))
                .OrderByDescending(p => p.PayId)
                .FirstOrDefaultAsync();

            var settle = new SettleRecord
            {
                OrderId = dto.OrderId,
                ActualRent = c.ActualRent,
                OverdueFee = c.OverdueFee,
                DamageFee = dto.DamageFee,
                RefundAmount = c.Refund,
                SupplementAmount = c.Supplement,
                Remark = dto.Remark,
                OperatorId = operatorId
            };
            _db.SettleRecords.Add(settle);

            // 结算记录落库、订单置「已完结」、车辆释放必须同进同退：
            // 否则结算单已生成但订单仍停在「已支付」，或订单完结了车还挂在「在租」，都会让后续对账与派单失真。
            order.OrderStatus = "已完结";
            order.ActualReturn = now;
            await _db.SaveChangesAsync();

            // 车辆释放走统一闸门：结算完成后本单不再占用车辆，
            // 但若这辆车已被新订单接管（极窄时间窗内可能发生），不能把正在被租走的车改回空闲。
            await VehicleLeaseGuard.TryReleaseAsync(_db, order.VehicleId, order.OrderId);

            // 退款必须和结算同进同退（需求 4.2.5「押金原路退还」）：
            //   · 支付宝真实通道 → 在这里调网关原路退回；失败则整笔结算回滚，
            //     订单仍是「租赁中」，前台可以修好配置后重试，不会出现"订单已完结、钱没退"的状态；
            //   · 模拟 / 微信 / 银行卡 → 只记账，并在返回文案里说清"未发生资金划转"。
            // 网关调用放在事务里，事务会多持有几百毫秒到十几秒；门店并发极低，可接受。
            //
            // 补缴分两种口径（仅当本次结算产生补缴金额时生效）：
            //   · 线下（默认）→ 顾客当场付款，结算单记录补缴金额即可，不生成线上账单；
            //   · 线上 → 生成一张独立的「线上补缴单」（待支付支付记录）并通知顾客回顾客端在线支付，
            //     顾客不想当场交钱时走这条路。两种都和结算同进同退。
            string note =
                (c.Supplement > 0 && dto.SupplementPayMethod.Equals("线上", StringComparison.Ordinal))
                    ? await CreateOnlineSupplementAsync(order, c)
                    : await SettleNoteAsync(order, c, paid);

            await tx.CommitAsync();
            return new SettleResult(settle, note);
        }
        catch (DbUpdateException ex) when (DbConcurrency.IsDuplicateKey(ex))
        {
            // 唯一索引 uk_settle_order 被触发：这张单已经有结算记录了。
            // 走到这里说明有人绕过了行锁（例如另一实例在锁释放后才提交），金额一分不能多退。
            await tx.RollbackAsync();
            _logger.LogWarning(ex, "订单 {OrderId} 重复结算被唯一索引拦截", dto.OrderId);
            throw new Exception("该订单已完成结算，请勿重复提交（如确需调整请走财务冲正流程）");
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 生成结算提示文案，并把退款真正执行掉。
    ///
    /// 单独拆出来是为了让 <see cref="Settle"/> 的事务体保持"改状态 → 释放车 → 退款 → 提交"的直线结构；
    /// 三种结果（已原路退回 / 无支付记录 / 仅登记应收补缴）互斥，文案必须让前台一眼看出
    /// **钱到底动没动**，不能统一回一句"结算成功"。
    ///
    /// 退款成功时同步向顾客推送「退款去向」通知（需求：提前还车/正常结算的退款按原支付渠道原路退回，
    /// 顾客需明确知道钱退到了哪里）——这是把"退款去向"落到顾客侧的关键一步。
    /// </summary>
    private async Task<string> SettleNoteAsync(RentalOrder order, SettleCalc c, PaymentRecord? paid)
    {
        if (c.Refund <= 0)
            return c.Supplement > 0
                ? $"结算完成；顾客需补缴 ¥{c.Supplement:0.00}（系统仅登记应收，未发起线上收款，请线下收取）。"
                : "结算完成，无需退款或补缴。";

        if (paid == null)
        {
            await _messages.SendAsync(
                "customer", order.CustomerId, "refund", order.OrderNo, "结算退款通知",
                $"订单 {order.OrderNo} 已结算，应退 ¥{c.Refund:0.00}，但未找到有效支付记录，门店将线下为您办理退款，请留意门店通知。");
            return $"应退 ¥{c.Refund:0.00}，但未找到该订单的有效支付记录，未执行退款，请财务核实。";
        }

        var r = await _payment.Refund(paid.PayId, c.Refund, $"订单 {order.OrderNo} 归还结算");

        // 向顾客说明退款去向：真实通道 = 原路退回；模拟/凭证不全 = 账面登记、请到门店领现金。
        var dest = r.RealMoney
            ? $"将按原支付渠道（{paid.PayType}）原路退回，预计到账 1-3 个工作日，请注意查收。"
            : $"当前为模拟收银台，退款 ¥{c.Refund:0.00} 已作账面登记，请到门店领取现金并留存凭证。";
        await _messages.SendAsync(
            "customer", order.CustomerId, "refund", order.OrderNo, "结算退款通知",
            $"订单 {order.OrderNo} 已结算，应退 ¥{c.Refund:0.00}。{dest}");

        return $"结算完成。{r.Message}";
    }

    /// <summary>
    /// 生成「线上补缴单」：为逾期/损坏产生的补缴金额创建一条独立的待支付支付记录
    /// （biz_type='supplement' + 全局唯一的 out_trade_no），并向顾客推送补缴通知。
    ///
    /// 与首付款的区别：首付款以租赁订单号作为 out_trade_no；补缴单必须拥有全局唯一的交易号
    /// （支付宝/微信会拒绝与历史交易重复的 out_trade_no），故单独生成并存储。
    /// 顾客回到顾客自助端后，用这张待支付记录发起与原首付款一致的真实通道支付。
    /// </summary>
    private async Task<string> CreateOnlineSupplementAsync(RentalOrder order, SettleCalc c)
    {
        var outTradeNo = BuildSupplementOutTradeNo(order.OrderNo);
        _db.PaymentRecords.Add(new PaymentRecord
        {
            OrderId = order.OrderId,
            PayAmount = c.Supplement,
            PayType = null,                 // 顾客在支付页自选，付款时再写入
            PayStatus = "待支付",
            BizType = "supplement",
            OutTradeNo = outTradeNo
        });
        await _db.SaveChangesAsync();

        await _messages.SendAsync(
            "customer", order.CustomerId, "supplement", order.OrderNo, "逾期补缴通知",
            $"订单 {order.OrderNo} 已结算，您尚有余款 ¥{c.Supplement:0.00} 待补缴（逾期/损坏费用）。" +
            $"门店已为您生成线上补缴单，请登录顾客端「我的订单 → 待补缴」在线支付，逾期可能影响后续租车。");

        return $"结算完成；已生成线上补缴单 ¥{c.Supplement:0.00} 并通知顾客在线支付（顾客回到顾客端即可缴款）。";
    }

    /// <summary>
    /// 生成全局唯一的补缴交易号：SP + 订单号 + 时间戳 + 随机后缀。
    /// 同一订单即便历史补缴已支付，也能保证 out_trade_no 不重复（通道拒绝重复订单号）。
    /// </summary>
    private static string BuildSupplementOutTradeNo(string orderNo)
    {
        var ts = DateTime.Now.ToString("yyyyMMddHHmmssfff");
        var rnd = new Random().Next(1000, 9999);
        return $"SP-{orderNo}-{ts}-{rnd}";
    }

    /// <summary>
    /// 财务对账：按日期范围核对「应收（订单租金+押金）」与「实收（支付记录）」，
    /// 同时统计退款、补缴、结算笔数，生成对账汇总。财务每日打烊前据此核对账目。
    /// </summary>
    public async Task<object> Reconcile(DateTime startDate, DateTime endDate)
    {
        if (endDate < startDate) throw new Exception("结束日期不能早于开始日期");
        var end = endDate.Date.AddDays(1).AddSeconds(-1); // 包含结束日全天

        // 该时间段内创建的订单（应收口径）。「已取消」的订单不产生应收义务，须剔除：
        // 否则「下单后取消」会被当成未收款项，正常经营也被算成差额。
        var orders = await _db.RentalOrders
            .Where(o => o.CreateTime >= startDate.Date && o.CreateTime <= end && o.OrderStatus != "已取消")
            .ToListAsync();

        // 实收口径必须与应收口径对齐到「同一批订单」——按 OrderId 关联这些订单名下的支付记录，
        // 而不是按支付时间另取一批。旧逻辑用 PayTime 取数，造成两类系统性错位：
        //   ①「订单今天建、次日才付款」→ 应收计了、实收没计，凭空多出差额；
        //   ②「往日订单今天才付款」→ 实收计了、应收没计，差额为负。
        // 于是按时收款、账目本该持平的门店，也永远显示「存在差额」。
        // 用半连接（EXISTS）而非 orderIds.Contains，避免大区间产生超大 IN 列表。
        // 每单仅一条支付记录（下单时建、支付时置为已支付），已支付口径不会重复计。
        var payments = await _db.PaymentRecords
            .Where(p => p.PayStatus == "已支付" && _db.RentalOrders.Any(o =>
                o.OrderId == p.OrderId &&
                o.CreateTime >= startDate.Date && o.CreateTime <= end &&
                o.OrderStatus != "已取消"))
            .ToListAsync();

        // 该时间段内的结算记录（退款/补缴口径）——按结算发生日统计当期资金流出
        var settles = await _db.SettleRecords
            .Where(s => s.SettleTime >= startDate.Date && s.SettleTime <= end)
            .ToListAsync();

        var expectedRent = orders.Sum(o => o.TotalRent);
        var expectedDeposit = orders.Sum(o => o.Deposit);
        var expectedTotal = expectedRent + expectedDeposit;

        // 实收只统计「已支付」的支付记录；待支付的款项不计入——它本就该形成差额，提示还有款未收
        var actualReceived = payments.Sum(p => p.PayAmount);
        var paidCount = payments.Count;

        var totalRefund = settles.Sum(s => s.RefundAmount);
        var totalSupplement = settles.Sum(s => s.SupplementAmount);
        var settleCount = settles.Count;

        // 净收入 = 实收 - 退款 + 补缴
        var netIncome = Math.Round(actualReceived - totalRefund + totalSupplement, 2);
        // 对账差异 = 应收 - 实收（正数表示少收，负数表示多收）
        var difference = Math.Round(expectedTotal - actualReceived, 2);

        return new
        {
            StartDate = startDate.Date.ToString("yyyy-MM-dd"),
            EndDate = endDate.Date.ToString("yyyy-MM-dd"),
            OrderCount = orders.Count,
            PaidCount = paidCount,
            SettleCount = settleCount,
            ExpectedRent = Math.Round(expectedRent, 2),
            ExpectedDeposit = Math.Round(expectedDeposit, 2),
            ExpectedTotal = Math.Round(expectedTotal, 2),
            ActualReceived = Math.Round(actualReceived, 2),
            TotalRefund = Math.Round(totalRefund, 2),
            TotalSupplement = Math.Round(totalSupplement, 2),
            NetIncome = netIncome,
            Difference = difference,
            IsBalanced = Math.Abs(difference) < 0.01m,
            Remark = Math.Abs(difference) < 0.01m ? "账目平衡" : difference > 0 ? $"存在 {difference:C} 差额（应收大于实收）" : $"存在 {-difference:C} 差额（实收大于应收）"
        };
    }
}

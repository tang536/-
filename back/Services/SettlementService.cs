using System.Data;
using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class SettlementService(
    AppDbContext db, PaymentService payment, MessageService messages,
    SupplementService supplement, ILogger<SettlementService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly PaymentService _payment = payment;
    private readonly MessageService _messages = messages;
    private readonly SupplementService _supplement = supplement;
    private readonly ILogger<SettlementService> _logger = logger;

    /// <summary>结算记录列表（对账用）</summary>
    public async Task<List<SettleListDto>> GetAll() =>
        await _db.SettleRecords
            .OrderByDescending(s => s.SettleTime)
            .Select(s => new SettleListDto
            {
                SettleId = s.SettleId,
                OrderId = s.OrderId,
                ActualRent = s.ActualRent,
                OverdueFee = s.OverdueFee,
                DamageFee = s.DamageFee,
                RefundAmount = s.RefundAmount,
                SupplementAmount = s.SupplementAmount,
                // 补缴收没收清必须看支付记录，不能看结算单上的"应收"：
                // 现金收款、支付宝/微信回调、顾客端在线补缴、查单兜底四条路径最终都落 payment_record，
                // 这里做同一个派生，前端不需要自己拼状态，也不会出现"钱收了状态没改"。
                SupplementPaid = s.RentalOrder == null ? 0m
                    : (s.RentalOrder.PaymentRecords
                        .Where(p => p.BizType == "supplement" && p.PayStatus == "已支付")
                        .Sum(p => (decimal?)p.PayAmount) ?? 0m),
                SupplementCollected = s.SupplementAmount <= 0 || (s.RentalOrder != null
                    && (s.RentalOrder.PaymentRecords
                        .Where(p => p.BizType == "supplement" && p.PayStatus == "已支付")
                        .Sum(p => (decimal?)p.PayAmount) ?? 0m) >= s.SupplementAmount - 0.01m),
                SupplementChannel = s.RentalOrder == null ? null
                    : s.RentalOrder.PaymentRecords
                        .Where(p => p.BizType == "supplement" && p.PayStatus == "已支付")
                        .OrderByDescending(p => p.PayId).Select(p => p.PayType).FirstOrDefault(),
                SupplementPendingPayId = s.RentalOrder == null ? null
                    : s.RentalOrder.PaymentRecords
                        .Where(p => p.BizType == "supplement" && p.PayStatus == "待支付")
                        .OrderByDescending(p => p.PayId).Select(p => (int?)p.PayId).FirstOrDefault(),
                SettleTime = s.SettleTime,
                OperatorId = s.OperatorId,
                Remark = s.Remark,
                RentalOrder = s.RentalOrder == null
                    ? null
                    : new SettleOrderBriefDto
                    {
                        OrderNo = s.RentalOrder.OrderNo,
                        VehicleNo = s.RentalOrder.Vehicle != null ? s.RentalOrder.Vehicle.VehicleNo : null,
                        CustomerName = s.RentalOrder.Customer != null ? s.RentalOrder.Customer.FullName : null,
                    },
            })
            .ToListAsync();

    /// <summary>顾客侧结算单列表：只返回自己订单的结算结果</summary>
    public async Task<List<MySettleDto>> GetMineAsync(int customerId)
    {
        var settles = await _db.SettleRecords
            .Where(s => s.RentalOrder != null && s.RentalOrder.CustomerId == customerId)
            .Include(s => s.RentalOrder).ThenInclude(o => o!.Vehicle)
            .Include(s => s.RentalOrder).ThenInclude(o => o!.PaymentRecords)
            .OrderByDescending(s => s.SettleTime)
            .ToListAsync();

        return settles.Select(s =>
        {
            var pays = s.RentalOrder?.PaymentRecords.Where(p => p.BizType == "supplement").ToList() ?? [];
            var paid = pays.Where(p => p.PayStatus == "已支付").ToList();
            var paidSum = paid.Sum(p => p.PayAmount);
            return new MySettleDto
            {
                SettleId = s.SettleId,
                OrderId = s.OrderId,
                OrderNo = s.RentalOrder?.OrderNo ?? "",
                VehicleNo = s.RentalOrder?.Vehicle?.VehicleNo ?? "",
                SettleTime = s.SettleTime,
                ActualRent = s.ActualRent,
                OverdueFee = s.OverdueFee,
                DamageFee = s.DamageFee,
                RefundAmount = s.RefundAmount,
                SupplementAmount = s.SupplementAmount,
                SupplementPaid = paidSum,
                SupplementCollected = s.SupplementAmount <= 0 || paidSum >= s.SupplementAmount - 0.01m,
                PaidChannel = paid.OrderByDescending(p => p.PayId).FirstOrDefault()?.PayType,
                PendingPayId = pays.Where(p => p.PayStatus == "待支付")
                    .OrderByDescending(p => p.PayId).Select(p => (int?)p.PayId).FirstOrDefault(),
                Remark = s.Remark,
            };
        }).ToList();
    }

    /// <summary>顾客选择在线支付补缴：确保有一张可付的待支付补缴单，再按选定渠道拉起收银台</summary>
    public async Task<CreatePayResultDto> PayOnlineAsync(int settleId, int customerId, string? channelRaw)
    {
        var payId = await _supplement.EnsurePendingSupplementAsync(settleId, customerId);
        if (string.IsNullOrWhiteSpace(channelRaw))
            return new CreatePayResultDto { PayId = payId };

        var r = await _supplement.PayAsync(payId, customerId, channelRaw);
        return new CreatePayResultDto
        {
            PayId = payId,
            OrderNo = r.OrderNo,
            PayType = r.PayType,
            Amount = r.Amount,
            PayUrl = r.PayUrl,
            QrCode = r.QrCode,
            PayMode = r.PayMode,
            RealPay = r.RealPay,
        };
    }

    /// <summary>结算核算结果</summary>
    private sealed record SettleCalc(
        decimal HourlyRent, decimal Multiplier,
        int ActualHours, decimal ActualRent,
        int OverdueHours, decimal OverdueFee, decimal RentTotal,
        decimal RentDiff, decimal Refund, decimal Supplement);

    /// <summary>统一的结算核算：正常租金只算到约定还车时间，超出部分按超时倍数计为逾期费</summary>
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

    /// <summary>结算试算（只读，不落库）</summary>
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
            order.MarkReturned(now);

            // 归还结算本身就是「还车」的最终落点：顾客提交过的还车申请必须在这里一并结清。
            // 不清的话会出现前后矛盾的幽灵单——订单已「已完结」，申请单却仍挂「待审核」，
            // 在「还车申请单」汇总表里长期占一行且带「审核」按钮；点通过只会得到
            // 「请完成归还结算」，而这张单已不可能再结算，等于永远关闭不掉。
            // 置 ""（已处理）跟审核通过同一口径，前端 filter(earlyReturnStatus) 会自动摘掉它。
            if (order.EarlyReturnStatus == "待审核") order.ClearEarlyReturnFlag();

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
            // 补缴的两种结算口径（仅当本次结算产生补缴金额时生效）：
            //   · 现金 → 只把"应收补缴"记到结算单，不建支付记录；前台随后的收款弹窗里
            //     「确认已收现金」才落一条已支付记录（钱真到手才算收清），没选就一直是「待收款」；
            //   · 支付宝/微信/银行卡 → 预生成一张待支付补缴单（指定渠道），顾客立刻能在
            //     顾客端「待补缴」付款，前台也可以直接把二维码摆给顾客扫。
            // 两种都和结算同进同退；网关调用一律不放进这个事务（见 SupplementService.CollectAsync）。
            string note;
            if (c.Supplement > 0)
            {
                // SupplementPayMethod 已由前端收敛为「现金/支付宝/微信/银行卡/待顾客选择」之一，
                // 与 SupplementService 的渠道归一保持一致。
                var channel = string.IsNullOrWhiteSpace(dto.SupplementPayMethod) ? SupplementPayMethods.Cash : dto.SupplementPayMethod;
                note = channel switch
                {
                    SupplementPayMethods.Cash => await SettleNoteAsync(order, c, paid),
                    SupplementPayMethods.CustomerChoice => await _supplement.NotifySupplementChoiceAsync(order, c.Supplement),
                    _ => await _supplement.CreateOnlineSupplementAsync(order, c.Supplement, channel),
                };
            }
            else
            {
                note = await SettleNoteAsync(order, c, paid);
            }

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

    /// <summary>生成结算提示文案，并把退款真正执行掉</summary>
    private async Task<string> SettleNoteAsync(RentalOrder order, SettleCalc c, PaymentRecord? paid)
    {
        /*
         * 前台看到的是一句人话，不是对账单：
         *   结算完成 · 已退 ¥985.40 至原支付渠道（支付宝）
         * 通道名、请求号、累计退款额属于财务对账信息，留在结算记录/小票里，
         * 不再往 toast 里塞（原来一句能拖成两行，前台根本没空读）。
         * 但「钱到底动没动」这一条必须保留——真退和账面登记不能糊成同一句话。
         */
        if (c.Refund <= 0)
            return c.Supplement > 0
                ? $"结算完成 · 顾客需补缴 ¥{c.Supplement:0.00}，请选择收款方式（现金 / 支付宝 / 微信 / 银行卡）"
                : "结算完成 · 无需退款或补缴";

        if (paid == null)
        {
            await _messages.SendAsync(
                "customer", order.CustomerId, "refund", order.OrderNo, "结算退款通知",
                $"订单 {order.OrderNo} 已结算，应退 ¥{c.Refund:0.00}，但未找到有效支付记录，门店将线下为您办理退款，请留意门店通知。");
            return $"结算完成 · 应退 ¥{c.Refund:0.00}，但无有效支付记录，请联系财务核实";
        }

        var r = await _payment.Refund(paid.PayId, c.Refund, $"订单 {order.OrderNo} 归还结算");

        // 向顾客说明退款去向：真实通道 = 原路退回；模拟/凭证不全 = 账面登记、请到门店领现金。
        var dest = r.RealMoney
            ? $"将按原支付渠道（{paid.PayType}）原路退回，预计到账 1-3 个工作日，请注意查收。"
            : $"当前为模拟收银台，退款 ¥{c.Refund:0.00} 已作账面登记，请到门店领取现金并留存凭证。";
        await _messages.SendAsync(
            "customer", order.CustomerId, "refund", order.OrderNo, "结算退款通知",
            $"订单 {order.OrderNo} 已结算，应退 ¥{c.Refund:0.00}。{dest}");

        return r.RealMoney
            ? $"结算完成 · 已退 ¥{c.Refund:0.00} 至原支付渠道（{paid.PayType}）"
            : $"结算完成 · 未真实划款，请线下退现金 ¥{c.Refund:0.00}";
    }

    /// <summary>财务对账：按日期范围核对「应收（订单租金+押金）」与「实收（支付记录）」， 同时统计退款、补缴、结算笔数，生成对账汇总</summary>
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
        //
        // 状态口径：**钱到过账**的四类支付都算实收——已支付 / 部分退款 / 已退款。
        // 旧逻辑只数「已支付」，把已结算订单整批漏掉：还车退押金后支付状态变为
        // 「部分退款」，钱明明收过却不计实收。实测差额 28,646.72 恰为 14 笔部分退款
        // 记录合计——门店每一单都按时收款，账面却永远「存在差额」。
        // 「已退款」同样计入（钱确实到过账），退款金额经 TotalRefund 冲减，净收入不变。
        // 补缴支付（biz_type='supplement'）不计入实收：它是额外费用、走下面「补缴」一行，
        // 混进实收会让「应收(租金+押金) − 实收」出现无来源的负数差额，且同一笔钱被计两次。
        var payments = await _db.PaymentRecords
            .Where(p => p.BizType == "order"
                && PayStatuses.MoneyArrived.Contains(p.PayStatus)
                && _db.RentalOrders.Any(o =>
                    o.OrderId == p.OrderId &&
                    o.CreateTime >= startDate.Date && o.CreateTime <= end &&
                    o.OrderStatus != "已取消"))
            .ToListAsync();

        // 补缴口径：只计**已到账**的补缴支付。旧逻辑取结算记录的 SupplementAmount
        // （结算时应收的补缴额），顾客没缴也被算进净收入——对账核对的是真金白银，
        // 未到账的钱是应收，不是收入。按需求 4.2.5「全量资金数据、对账零偏差」，
        // 净收入里出现的每一分钱都必须是到过账的。
        var paidSupplement = await _db.PaymentRecords
            .Where(p => p.BizType == "supplement" && p.PayStatus == "已支付"
                && _db.RentalOrders.Any(o =>
                    o.OrderId == p.OrderId &&
                    o.CreateTime >= startDate.Date && o.CreateTime <= end &&
                    o.OrderStatus != "已取消"))
            .SumAsync(p => p.PayAmount);

        // 该时间段内的结算记录——只用于统计结算笔数；退款/补缴金额一律以支付记录为准（见下）
        var settles = await _db.SettleRecords
            .Where(s => s.SettleTime >= startDate.Date && s.SettleTime <= end)
            .ToListAsync();

        var expectedRent = orders.Sum(o => o.TotalRent);
        var expectedDeposit = orders.Sum(o => o.Deposit);
        var expectedTotal = expectedRent + expectedDeposit;

        // 实收只统计「钱到过账」的支付记录；待支付的款项不计入——它本就该形成差额，提示还有款未收
        var actualReceived = payments.Sum(p => p.PayAmount);
        var paidCount = payments.Count;

        // 退款口径：只认支付记录上**实际执行**的累计退款（refund_amount），与实收同一张表、
        // 同一批订单——对账核对的是真金白银，资金流出一分一毫都要有支付记录作证。
        // 旧逻辑取结算记录的 RefundAmount（结算时「承诺退多少」）：历史上有 10 笔早期结算
        // 只写了结算单、退款没落到支付记录（合计 80,733.35，支付记录至今仍是已支付/退款 0），
        // 按承诺口径会把从没发生过的退款计成资金流出，净收入凭空少一半；
        // 且与同页面「财务下钻」卡（支付口径）的退款数对不上，两张卡两个数。
        // 当前代码退款与结算同事务落账（PaymentService.Refund 两条路径都写 refund_amount，
        // 失败即整体回滚），两个来源必然一致；取支付记录同时满足「钱真的动了」与「和下钻同源」。
        var totalRefund = payments.Sum(p => p.RefundAmount);
        var totalSupplement = paidSupplement;
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

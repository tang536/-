using System.Data;
using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class SettlementService(AppDbContext db, PaymentService payment, MessageService messages, SupplementService supplement, ImageStorage images, LogService logs, ILogger<SettlementService> logger)
{
    ///<summary>支付记录的业务类型：补缴</summary>
    private const string BizTypeSupplement = "supplement";

    ///<summary>损坏费自动转维保的阈值配置键（元；0 表示一律不转）</summary>
    private const string KeyDamageMaintThreshold = "damage_maintenance_threshold";

    private readonly AppDbContext _db = db;
    private readonly PaymentService _payment = payment;
    private readonly MessageService _messages = messages;
    private readonly SupplementService _supplement = supplement;
    private readonly ImageStorage _images = images;
    private readonly LogService _logs = logs;
    private readonly ILogger<SettlementService> _logger = logger;

    ///<summary>
    /// 结算记录分页查询（对账用）：联表与补缴聚合由数据库视图 v_settlement_bill 一次算好。
    ///
    /// 为什么从「一次全量返回」改成分页：结算单只会越攒越多（门店一天几单，一年就是上千行），
    /// 全量塞给浏览器再由前端本地筛选，首屏迟早会被这一坨 JSON 拖住；日期区间也该由库来裁。
    /// 口径不变：仍按结算时间倒序，`from/to` 传空即全部历史。
    ///</summary>
    public async Task<SettlePageDto> GetPaged(DateTime? from, DateTime? to, int page, int pageSize, string? keyword = null)
    {
        var q = _db.SettlementBills.AsNoTracking();
        //区间用 [from 当天 00:00, to 次日 00:00)：日期框只给到天，按「含当天」理解才不会漏掉当天最后几单
        if (from is { } f) { var start = f.Date; q = q.Where(b => b.SettleTime >= start); }
        if (to is { } t) { var end = t.Date.AddDays(1); q = q.Where(b => b.SettleTime < end); }
        //关键词也交给库：订单号 / 车牌 / 客户名，前端只保留输入框，不再拿全量数据在本地筛。
        //结算单号按「JS000074」展示，搜的时候把前缀剥掉直接比 ID（库里的字符串拼接没法翻译成 SQL）
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var k = keyword.Trim();
            var raw = k.StartsWith("JS", StringComparison.OrdinalIgnoreCase) ? k[2..] : k;
            var sid = raw.Length > 0 && raw.All(char.IsDigit) && int.TryParse(raw, out var parsed) ? parsed : (int?)null;

            q = q.Where(b =>
                (b.OrderNo != null && b.OrderNo.Contains(k)) ||
                (b.VehicleNo != null && b.VehicleNo.Contains(k)) ||
                (b.CustomerName != null && b.CustomerName.Contains(k)) ||
                (sid != null && b.SettleId == sid));
        }

        var total = await q.CountAsync();
        var (_, _, skip, take) = Paging.Normalize(page, pageSize);
        //取 0 表示「不分页」（Paging.Normalize 的既有约定），对账导出类调用才用得到
        var ordered = q.OrderByDescending(b => b.SettleTime).ThenByDescending(b => b.SettleId);
        var rows = take > 0 ? await ordered.Skip(skip).Take(take).ToListAsync() : await ordered.ToListAsync();

        return new SettlePageDto
        {
            Total = total,
            Page = page < 1 ? 1 : page,
            PageSize = pageSize,
            Items = rows.Select(b => new SettleListDto
            {
                SettleId = b.SettleId,
                OrderId = b.OrderId,
                ActualRent = b.ActualRent,
                OverdueFee = b.OverdueFee,
                DamageFee = b.DamageFee,
                RefundAmount = b.RefundAmount,
                SupplementAmount = b.SupplementAmount,
                SupplementPaid = b.SupplementPaid,
                SupplementCollected = b.SupplementCollected == 1,
                SupplementChannel = b.SupplementChannel,
                SupplementPendingPayId = b.SupplementPendingPayId,
                SettleTime = b.SettleTime,
                OperatorId = b.OperatorId,
                Remark = b.Remark,
                RentalOrder = new SettleOrderBriefDto
                {
                    OrderNo = b.OrderNo,
                    VehicleNo = b.VehicleNo,
                    CustomerName = b.CustomerName,
                }
            }).ToList()
        };
    }

    ///<summary>某订单的交接验车记录（取车 / 还车各最多一条，按登记顺序返回）</summary>
    public async Task<List<HandoverDto>> ListHandovers(int orderId)
    {
        var rows = await _db.OrderHandovers.AsNoTracking()
            .Where(h => h.OrderId == orderId)
            .OrderBy(h => h.HandoverId)
            .ToListAsync();
        return rows.Select(ToHandoverDto).ToList();
    }

    ///<summary>
    /// 登记 / 覆盖一条交接快照：同一订单同一阶段只留一条（唯一键 uk_order_phase），
    /// 照片与备注按「追加」合并——门店往往是先拍两张，再补一张划痕特写。
    /// </summary>
    public async Task<HandoverDto> SaveHandover(SaveHandoverDto dto, int operatorId)
    {
        //订单必须先存在，否则就是前端串单了；这里不做状态限制：
        //取车快照在「已支付」时登记，还车快照在结算瞬间登记，两种状态都要能写。
        if (!await _db.RentalOrders.AnyAsync(o => o.OrderId == dto.OrderId))
            throw new BusinessException("订单不存在，无法登记验车");

        var existing = await _db.OrderHandovers
            .FirstOrDefaultAsync(h => h.OrderId == dto.OrderId && h.Phase == dto.Phase);
        if (existing == null)
        {
            existing = OrderHandover.Create(dto.OrderId, dto.Phase, dto.BatteryLevel, dto.PhotoPaths, dto.ConditionNote, operatorId);
            _db.OrderHandovers.Add(existing);
        }
        else
        {
            /*
             * 覆盖时留审计：验车快照正是损坏费的唯一依据，
             * 「谁在什么时候把电量从 35% 改成 90%、把照片从 1 张补到 3 张」必须能查。
             * 这不是怀疑前台，而是顾客投诉时门店能自证流程没被改过。
             */
            var before = $"电量 {existing.BatteryLevel?.ToString() ?? "未记"}%、照片 {CountPhotos(existing.PhotoPaths)} 张";
            existing.Overwrite(dto.BatteryLevel, dto.PhotoPaths, dto.ConditionNote, operatorId);
            var after = $"电量 {dto.BatteryLevel?.ToString() ?? "未记"}%、照片 {CountPhotos(dto.PhotoPaths ?? existing.PhotoPaths)} 张";
            if (before != after)
            {
                await _db.SaveChangesAsync();//先落库再写日志：日志失败不该让覆盖操作回滚
                await _logs.WriteLog(operatorId, $"覆盖订单 {dto.OrderId} 的{dto.Phase}验车：{before} → {after}", "验车审计", "成功");
                return ToHandoverDto(existing);
            }
        }
        await _db.SaveChangesAsync();
        return ToHandoverDto(existing);
    }

    ///<summary>照片张数（逗号分隔）</summary>
    private static int CountPhotos(string? paths) =>
        string.IsNullOrWhiteSpace(paths) ? 0 : paths.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;

    ///<summary>验车照片落盘：子目录 handover，文件名带「订单号_阶段」，出事后不用反查数据库</summary>
    public async Task<string> SaveHandoverPhoto(IFormFile? file, int orderId, string? phase) =>
        await _images.SaveAsync(file, "handover", $"Handover_{orderId}_{(string.IsNullOrWhiteSpace(phase) ? "unknown" : phase)}");

    private static HandoverDto ToHandoverDto(OrderHandover h) => new()
    {
        HandoverId = h.HandoverId,
        OrderId = h.OrderId,
        Phase = h.Phase,
        BatteryLevel = h.BatteryLevel,
        Photos = string.IsNullOrWhiteSpace(h.PhotoPaths)
            ? []
            : h.PhotoPaths.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
        ConditionNote = h.ConditionNote,
        OperatorId = h.OperatorId,
        CreateTime = h.CreateTime,
    };

    ///<summary>顾客侧结算单列表：只返回自己订单的结算结果</summary>
    public async Task<List<MySettleDto>> GetMineAsync(int customerId)
    {
        return await _db.SettlementBills
            .AsNoTracking()
            .Where(b => b.CustomerId == customerId)
            .OrderByDescending(b => b.SettleTime)
            .Select(b => new MySettleDto
            {
                SettleId = b.SettleId,
                OrderId = b.OrderId,
                OrderNo = b.OrderNo ?? "",
                VehicleNo = b.VehicleNo ?? "",
                SettleTime = b.SettleTime,
                ActualRent = b.ActualRent,
                OverdueFee = b.OverdueFee,
                DamageFee = b.DamageFee,
                RefundAmount = b.RefundAmount,
                SupplementAmount = b.SupplementAmount,
                SupplementPaid = b.SupplementPaid,
                SupplementCollected = b.SupplementCollected == 1,
                PaidChannel = b.SupplementChannel,
                PendingPayId = b.SupplementPendingPayId,
                Remark = b.Remark,
            })
            .ToListAsync();
    }

    ///<summary>顾客选择在线支付补缴：确保有一张可付的待支付补缴单，再按选定渠道拉起收银台</summary>
    public async Task<CreatePayResultDto> PayOnlineAsync(int settleId, int customerId, string? channelRaw)
    {
        var payId = await _supplement.EnsurePendingSupplementAsync(settleId, customerId);
        if (string.IsNullOrWhiteSpace(channelRaw)) return new CreatePayResultDto { PayId = payId };

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

    ///<summary>结算核算结果</summary>
    private sealed record SettleCalc(decimal HourlyRent, decimal Multiplier, int ActualHours, decimal ActualRent, int OverdueHours, decimal OverdueFee, decimal RentTotal, decimal RentDiff, decimal Refund, decimal Supplement);

    ///<summary>结算所需的订单快照：试算从视图取，正式结算从锁内实体取，核算逻辑只认这几列</summary>
    private readonly record struct SettleSnapshot(int VehicleId, DateTime LeaseStart, DateTime LeaseEnd, decimal TotalRent, decimal Deposit);

    ///<summary>损坏理赔工单号：WB+时间戳，撞号重试（与 MaintenanceService 同一套规则，轮换同一个号段）</summary>
    private async Task<string> NextDamageWorkOrderNoAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            var no = "WB" + AppClock.Now.ToString("yyyyMMddHHmmssfff");
            if (!await _db.MaintenanceOrders.AnyAsync(x => x.WorkOrderNo == no)) return no;
            await Task.Delay(2);
        }
        return "WB" + Guid.NewGuid().ToString("N")[..17];
    }

    ///<summary>损坏费转维保的金额门槛（读系统配置，缺项按 ¥100 兜底）</summary>
    private async Task<decimal> DamageThresholdAsync()
    {
        var cfgs = await _db.SystemConfigs.Where(c => c.ConfigKey == KeyDamageMaintThreshold).ToListAsync();
        return ConfigValue.Decimal(cfgs, KeyDamageMaintThreshold, 100m);
    }

    ///<summary>统一结算核算：正常租金只算到约定还车时间，超出部分按超时倍数计为逾期费</summary>
    private async Task<SettleCalc> CalculateAsync(SettleSnapshot order, decimal damageFee)
    {
        var now = AppClock.Now;
        //计费粒度与下单一致：按小时（向上取整，最少 1 小时）
        var (_, hourlyRent, _) = await PricingResolver.ResolveHourlyAsync(_db, order.VehicleId);

        //正常租金只算到约定还车时间（leaseEnd）为止
        var normalEnd = order.LeaseEnd < now ? order.LeaseEnd : now;
        var actualHours = PricingResolver.CalcHours(order.LeaseStart, normalEnd);
        var actualRent = Math.Round(actualHours * hourlyRent, 2);

        //未逾期时这里就是 0：「不收」也必须有金额，不能只靠一句规则说明带过
        var overdueHours = order.LeaseEnd < now ? PricingResolver.CalcHours(order.LeaseEnd, now) : 0;
        var multiplierCfgs = await _db.SystemConfigs.Where(c => c.ConfigKey == "overdue_multiplier").ToListAsync();
        var multiplier = ConfigValue.Decimal(multiplierCfgs, "overdue_multiplier", 1.5m);
        var overdueFee = Math.Round(overdueHours * hourlyRent * multiplier, 2);

        //落库仍拆成 actual_rent/overdue_fee 两列，是为了保留可审计的构成
        var (rentTotal, rentDiff, refund, supplementFee) = SettlementMath.Settle(order.TotalRent, order.Deposit, damageFee, actualRent, overdueFee);

        return new SettleCalc(hourlyRent, multiplier, actualHours, actualRent, overdueHours, overdueFee, rentTotal, rentDiff, refund, supplementFee);
    }

    ///<summary>结算试算：订单摘要从视图窄取一列，不加载客户/车辆整实体</summary>
    public async Task<SettlePreviewDto> Preview(int orderId)
    {
        var order = await _db.OrderOverviews.AsNoTracking()
            .Where(o => o.OrderId == orderId)
            .Select(o => new { o.OrderId, o.OrderNo, o.CustomerName, o.VehicleNo, o.VehicleId, o.LeaseStart, o.LeaseEnd, o.TotalRent, o.Deposit })
            .FirstOrDefaultAsync()
            ?? throw new BusinessException("订单不存在");

        var c = await CalculateAsync(
            new SettleSnapshot(order.VehicleId, order.LeaseStart, order.LeaseEnd, order.TotalRent, order.Deposit),
            0m);
        return new SettlePreviewDto
        {
            OrderId = order.OrderId,
            OrderNo = order.OrderNo,
            CustomerName = order.CustomerName ?? "",
            VehicleNo = order.VehicleNo ?? "",
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

    ///<summary>结算结果：单据摘要+给前台的提示（尤其是退款是否真的原路退回）</summary>
    public sealed record SettleResult(SettleResultDto Record, string Message);

    ///<summary>结算实体 → 响应投影：只回传前端弹窗与提示需要的金额构成</summary>
    private static SettleResultDto ToResultDto(SettleRecord s) => new()
    {
        SettleId = s.SettleId,
        OrderId = s.OrderId,
        ActualRent = s.ActualRent,
        OverdueFee = s.OverdueFee,
        DamageFee = s.DamageFee,
        RefundAmount = s.RefundAmount,
        SupplementAmount = s.SupplementAmount,
        NeedCollect = s.SupplementAmount > 0
    };

    public async Task<SettleResult> Settle(SettleOrderDto dto, int operatorId)
    {
        //结算会碰三样「不能重复发生」的东西：结算单、押金退款、车辆释放
        return await DbConcurrency.RunAsync(() => SettleOnce(dto, operatorId), _logger, "订单结算", reset: () => _db.ChangeTracker.Clear());
    }

    private async Task<SettleResult> SettleOnce(SettleOrderDto dto, int operatorId)
    {
        if (dto.DamageFee < 0) throw new BusinessException("损坏费不能为负数");

        //READ COMMITTED：结算事务里只锁「自己读到的那一行」
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            //等锁上限压到 5 秒（MySQL 默认 50 秒会让请求线程与连接池一起被拖住）
            await DbRowLock.PrepareAsync(_db);

            //锁内重读：这一句之后的 order 才是「当前真实状态」，事务外读到的都可能已过期
            var order = await DbRowLock.LockOrderAsync(_db, dto.OrderId) ?? throw new BusinessException("订单不存在");
            if (order.OrderStatus is not ("已支付" or "租赁中")) throw new BusinessException($"订单当前状态为「{order.OrderStatus}」，不可结算" +
                                                                               "（可能已被其他同事结算或取消，请刷新后查看）");

            var c = await CalculateAsync(
                new SettleSnapshot(order.VehicleId, order.LeaseStart, order.LeaseEnd, order.TotalRent, order.Deposit),
                dto.DamageFee);
            var now = AppClock.Now;

            //本单成功支付的那条记录
            var paid = await _db.PaymentRecords.Where(p => p.OrderId == order.OrderId && (p.PayStatus == "已支付" || p.PayStatus == "部分退款")).OrderByDescending(p => p.PayId).FirstOrDefaultAsync();

            var settle = SettleRecord.Create(
                (dto.OrderId, operatorId),
                (c.ActualRent, c.OverdueFee, dto.DamageFee),
                (c.Refund, c.Supplement, dto.Remark),
                now);
            _db.SettleRecords.Add(settle);

            //结算记录落库、订单置「已完结」、车辆释放必须同进同退
            order.MarkReturned(now);

            //归还结算本身就是「还车」的最终落点：顾客提交过的还车申请必须在这里一并结清
            if (order.EarlyReturnStatus == "待审核") order.ClearEarlyReturnFlag();

            await _db.SaveChangesAsync();

            //车辆释放走统一闸门
            await VehicleLeaseGuard.TryReleaseAsync(_db, order.VehicleId, order.OrderId);

            /*
             * 损坏费 > 0 就要留下「这车得修」的证据链。
             * 门店最常见的漏损是：赔偿收了、车带伤继续出租，下一位顾客再赔一次，
             * 同一处划痕收三遍钱，最后账上有收入、车却一直没修。
             * 所以工单一定建（可追溯、维修费能与赔偿对账）；
             * 只有金额达到阈值（默认 ¥100，系统配置 damage_maintenance_threshold）才把车锁进「维保中」——
             * 小额划痕门店通常根本不修，不该因为一笔赔偿就让车下线。
             */
            if (dto.DamageFee > 0)
            {
                var damageOrder = MaintenanceOrder.Report(
                    await NextDamageWorkOrderNoAsync(),
                    order.VehicleId,
                    "归还损坏",
                    $"归还结算 JS{settle.SettleId:D6} · 订单 {order.OrderNo}，损坏费 ¥{dto.DamageFee:0.00}"
                    + (string.IsNullOrWhiteSpace(dto.Remark) ? "" : $"；备注：{dto.Remark.Trim()}"),
                    null,
                    operatorId);
                _db.MaintenanceOrders.Add(damageOrder);
                await _db.SaveChangesAsync();

                var threshold = await DamageThresholdAsync();
                if (threshold > 0 && dto.DamageFee >= threshold
                    && !await VehicleStateGuard.TryEnterMaintenanceAsync(_db, order.VehicleId))
                {
                    //车已经被别的在途工单锁着、或刚被租走：工单照留，不抢状态
                    _logger.LogWarning("损坏费转维保：车辆 {VehicleId} 未能进入「维保中」（已有在途工单或状态已变更）", order.VehicleId);
                }
            }

            //退款必须和结算同进同退：· 支付宝真实通道 → 在这里调网关原路退回
            string note;
            if (c.Supplement > 0)
            {
                //SupplementPayMethod 已由前端收敛为「现金/支付宝/微信/银行卡/待顾客选择」之一
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

            //结算之后的旁路动作（审计 + 通知）不能让已提交的结算变成失败，
            //所以单独 try：写不进去只记 warning，账已经落了。
            try
            {
                //结算审计：改的是哪笔钱、由谁定的、构成如何——财务对账/顾客投诉都要能追到人
                await _logs.WriteLog(operatorId,
                    $"结算 JS{settle.SettleId:D6} · 订单 {order.OrderNo}：实际租金 ¥{c.ActualRent:0.00}（{c.ActualHours} 小时）、"
                    + $"逾期费 ¥{c.OverdueFee:0.00}（{c.OverdueHours} 小时×{c.Multiplier}）、损坏费 ¥{dto.DamageFee:0.00}、"
                    + $"退款 ¥{c.Refund:0.00}、补缴 ¥{c.Supplement:0.00}",
                    "结算审计", "成功");

                //赔付/逾期明细主动告知顾客：钱被扣了却没人解释，是差评的最大来源。
                //消息里点明「可调取验车照片核对」，把争执从柜台拉到证据上。
                if (dto.DamageFee > 0 || c.OverdueFee > 0)
                {
                    var parts = new List<string>();
                    if (c.OverdueFee > 0) parts.Add($"逾期 {c.OverdueHours} 小时，逾期费 ¥{c.OverdueFee:0.00}");
                    if (dto.DamageFee > 0) parts.Add($"车辆损坏赔付 ¥{dto.DamageFee:0.00}");
                    var tail = c.Supplement > 0
                        ? $"本次需补缴 ¥{c.Supplement:0.00}"
                        : (c.Refund > 0 ? $"应退 ¥{c.Refund:0.00}" : "无需退款或补缴");
                    await _messages.SendAsync(
                        ("customer", order.CustomerId),
                        ("settle-detail", settle.SettleId.ToString()),
                        ("结算费用明细", $"订单 {order.OrderNo} 已结算：{string.Join("；", parts)}。{tail}。如对损坏费有异议，可到门店调取取车/还车验车照片核对。"),
                        ("warning", "/my-orders", false));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "结算 {SettleId} 的审计/通知写入失败（结算本身已成功）", settle.SettleId);
            }

            return new SettleResult(ToResultDto(settle), note);
        }
        catch (DbUpdateException ex) when (DbConcurrency.IsDuplicateKey(ex))
        {
            //唯一索引 uk_settle_order 被触发：这张单已经有结算记录了
            await tx.RollbackAsync();
            _logger.LogWarning(ex, "订单 {OrderId} 重复结算被唯一索引拦截", dto.OrderId);
            throw new BusinessException("该订单已完成结算，请勿重复提交（如确需调整请走财务冲正流程）");
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    ///<summary>生成结算提示文案，并把退款真正执行掉</summary>
    private async Task<string> SettleNoteAsync(RentalOrder order, SettleCalc c, PaymentRecord? paid)
    {
        /*
         * 前台看到的是一句人话，不是对账单：
         *   结算完成 · 已退 ¥985.40 至原支付渠道（支付宝）
         * 通道名、请求号、累计退款额属于财务对账信息，留在结算记录/小票里，
         * 不再往 toast 里塞（原来一句能拖成两行，前台根本没空读）。
         * 但「钱到底动没动」这一条必须保留——真退和账面登记不能糊成同一句话。
         */
        if (c.Refund <= 0) return c.Supplement > 0 ? $"结算完成 · 顾客需补缴 ¥{c.Supplement:0.00}，请选择收款方式（现金 / 支付宝 / 微信 / 银行卡）" : "结算完成 · 无需退款或补缴";

        if (paid == null)
        {
            await _messages.SendAsync(
                ("customer", order.CustomerId),
                ("refund", order.OrderNo),
                ("结算退款通知", $"订单 {order.OrderNo} 已结算，应退 ¥{c.Refund:0.00}，但未找到有效支付记录，门店将线下为您办理退款，请留意门店通知。"));
            return $"结算完成 · 应退 ¥{c.Refund:0.00}，但无有效支付记录，请联系财务核实";
        }

        var r = await _payment.Refund(paid.PayId, c.Refund, $"订单 {order.OrderNo} 归还结算");

        //向顾客说明退款去向：真实通道=原路退回
        var dest = r.RealMoney ? $"将按原支付渠道（{paid.PayType}）原路退回，预计到账 1-3 个工作日，请注意查收。" : $"当前为模拟收银台，退款 ¥{c.Refund:0.00} 已作账面登记，请到门店领取现金并留存凭证。";
        await _messages.SendAsync(
            ("customer", order.CustomerId),
            ("refund", order.OrderNo),
            ("结算退款通知", $"订单 {order.OrderNo} 已结算，应退 ¥{c.Refund:0.00}。{dest}"));

        return r.RealMoney ? $"结算完成 · 已退 ¥{c.Refund:0.00} 至原支付渠道（{paid.PayType}）" : $"结算完成 · 未真实划款，请线下退现金 ¥{c.Refund:0.00}";
    }

    ///<summary>财务对账：按日期范围核对「应收（订单租金+押金）」与「实收（支付记录）」</summary>
    public async Task<object> Reconcile(DateTime startDate, DateTime endDate)
    {
        if (endDate < startDate) throw new BusinessException("结束日期不能早于开始日期");
        var end = endDate.Date.AddDays(1).AddSeconds(-1);//包含结束日全天

        //该时间段内创建的订单（应收口径）
        var orders = await _db.RentalOrders.Where(o => o.CreateTime >= startDate.Date && o.CreateTime <= end && o.OrderStatus != "已取消").ToListAsync();

        //实收口径必须与应收口径对齐到「同一批订单」
        var payments = await _db.PaymentRecords.Where(p => p.BizType == "order" && PayStatuses.MoneyArrived.Contains(p.PayStatus) && _db.RentalOrders.Any(o => o.OrderId == p.OrderId && o.CreateTime >= startDate.Date && o.CreateTime <= end && o.OrderStatus != "已取消")).ToListAsync();

        //补缴口径：只计已到账的补缴支付
        var paidSupplement = await _db.PaymentRecords.Where(p => p.BizType == BizTypeSupplement && p.PayStatus == "已支付" && _db.RentalOrders.Any(o => o.OrderId == p.OrderId && o.CreateTime >= startDate.Date && o.CreateTime <= end && o.OrderStatus != "已取消")).SumAsync(p => p.PayAmount);

        //该时间段内的结算记录
        var settles = await _db.SettleRecords.Where(s => s.SettleTime >= startDate.Date && s.SettleTime <= end).ToListAsync();

        var expectedRent = orders.Sum(o => o.TotalRent);
        var expectedDeposit = orders.Sum(o => o.Deposit);
        var expectedTotal = expectedRent + expectedDeposit;

        //实收只统计「钱到过账」的支付记录
        var actualReceived = payments.Sum(p => p.PayAmount);
        var paidCount = payments.Count;

        //退款口径：只认支付记录上实际执行的累计退款（refund_amount）
        var totalRefund = payments.Sum(p => p.RefundAmount);
        var totalSupplement = paidSupplement;
        var settleCount = settles.Count;

        //净收入=实收-退款+补缴
        var netIncome = Math.Round(actualReceived - totalRefund + totalSupplement, 2);
        //对账差异=应收-实收（正数表示少收，负数表示多收）
        var difference = Math.Round(expectedTotal - actualReceived, 2);

        //差异文案单独算：三级判断用 switch 表达式，既不嵌套三元（Sonar S3358）
        var remark = difference switch
        {
            _ when Math.Abs(difference) < 0.01m => "账目平衡",
            > 0 => $"存在 {difference:C} 差额（应收大于实收）",
            _ => $"存在 {-difference:C} 差额（实收大于应收）"
        };

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
            Remark = remark
        };
    }
}

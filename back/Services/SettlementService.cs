using System.Data;
using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class SettlementService(AppDbContext db,PaymentService payment,MessageService messages,SupplementService supplement,ILogger<SettlementService> logger)
{
    ///<summary>支付记录的业务类型：补缴</summary>
    private const string BizTypeSupplement="supplement";

    private readonly AppDbContext _db=db;
    private readonly PaymentService _payment=payment;
    private readonly MessageService _messages=messages;
    private readonly SupplementService _supplement=supplement;
    private readonly ILogger<SettlementService> _logger=logger;

    ///<summary>结算记录列表（对账用）</summary>
    public async Task<List<SettleListDto>> GetAll()
    {
        //排序留在库里、口径与原来完全一致（只按结算时间倒序，不再追加任何 tie-breaker）
        var records=await _db.SettleRecords
            .AsNoTracking()
            .Include(s=>s.RentalOrder).ThenInclude(o=>o!.PaymentRecords)
            .Include(s=>s.RentalOrder).ThenInclude(o=>o!.Vehicle)
            .Include(s=>s.RentalOrder).ThenInclude(o=>o!.Customer)
            .OrderByDescending(s=>s.SettleTime)
            .ToListAsync();

        return records.Select(ToSettleListDto).ToList();
    }

    ///<summary>单条结算记录 → 列表行</summary>
    private static SettleListDto ToSettleListDto(SettleRecord s)
    {
        var paidSupplement=PaidSupplementOf(s.RentalOrder);

        return new SettleListDto
        {
            SettleId=s.SettleId,
            OrderId=s.OrderId,
            ActualRent=s.ActualRent,
            OverdueFee=s.OverdueFee,
            DamageFee=s.DamageFee,
            RefundAmount=s.RefundAmount,
            SupplementAmount=s.SupplementAmount,
            SupplementPaid=paidSupplement,
            //没有订单时不能只凭金额判断
            SupplementCollected=s.SupplementAmount<=0||(s.RentalOrder!=null&&paidSupplement>=s.SupplementAmount-0.01m),
            SupplementChannel=LatestSupplement(s.RentalOrder,"已支付")?.PayType,
            SupplementPendingPayId=LatestSupplement(s.RentalOrder,"待支付")?.PayId,
            SettleTime=s.SettleTime,
            OperatorId=s.OperatorId,
            Remark=s.Remark,
            RentalOrder=ToSettleOrderBrief(s.RentalOrder),
        };
    }

    ///<summary>已到账的补缴合计（现金与线上同口径：只看支付记录的实收金额）</summary>
    private static decimal PaidSupplementOf(RentalOrder? order)
        => order==null?0m:order.PaymentRecords.Where(IsSupplementPay).Sum(p=>p.PayAmount);

    ///<summary>取最近一张某种状态的补缴单：按自增主键倒序，与「后发生的覆盖先发生的」一致</summary>
    private static PaymentRecord? LatestSupplement(RentalOrder? order,string payStatus)
        => order?.PaymentRecords.Where(p=>IsSupplementPay(p)&&p.PayStatus==payStatus)
            .OrderByDescending(p=>p.PayId)
            .FirstOrDefault();

    ///<summary>补缴支付记录的判定：业务类型为补缴且钱已到账</summary>
    private static bool IsSupplementPay(PaymentRecord p)
        => p.BizType==BizTypeSupplement&&p.PayStatus=="已支付";

    ///<summary>订单摘要：订单可能已被删除（历史数据），为空时整层返回 null，前端按「无关联订单」处理</summary>
    private static SettleOrderBriefDto? ToSettleOrderBrief(RentalOrder? order)
        => order==null?null:new SettleOrderBriefDto
        {
            OrderNo=order.OrderNo,
            VehicleNo=order.Vehicle?.VehicleNo,
            CustomerName=order.Customer?.FullName,
        };

    ///<summary>顾客侧结算单列表：只返回自己订单的结算结果</summary>
    public async Task<List<MySettleDto>> GetMineAsync(int customerId)
    {
        var settles=await _db.SettleRecords.Where(s=>s.RentalOrder!=null&&s.RentalOrder.CustomerId==customerId).Include(s=>s.RentalOrder).ThenInclude(o=>o!.Vehicle).Include(s=>s.RentalOrder).ThenInclude(o=>o!.PaymentRecords).OrderByDescending(s=>s.SettleTime).ToListAsync();

        return settles.Select(s =>
        {
            var pays=s.RentalOrder?.PaymentRecords.Where(p=>p.BizType==BizTypeSupplement).ToList()??[];
            var paid=pays.Where(p=>p.PayStatus=="已支付").ToList();
            var paidSum=paid.Sum(p=>p.PayAmount);
            return new MySettleDto
            {
                SettleId=s.SettleId,
                OrderId=s.OrderId,
                OrderNo=s.RentalOrder?.OrderNo??"",
                VehicleNo=s.RentalOrder?.Vehicle?.VehicleNo??"",
                SettleTime=s.SettleTime,
                ActualRent=s.ActualRent,
                OverdueFee=s.OverdueFee,
                DamageFee=s.DamageFee,
                RefundAmount=s.RefundAmount,
                SupplementAmount=s.SupplementAmount,
                SupplementPaid=paidSum,
                SupplementCollected=s.SupplementAmount<=0||paidSum>=s.SupplementAmount-0.01m,
                PaidChannel=paid.OrderByDescending(p=>p.PayId).FirstOrDefault()?.PayType,
                PendingPayId=pays.Where(p=>p.PayStatus=="待支付").OrderByDescending(p=>p.PayId).Select(p=>(int?)p.PayId).FirstOrDefault(),
                Remark=s.Remark,
            };
        }).ToList();
    }

    ///<summary>顾客选择在线支付补缴：确保有一张可付的待支付补缴单，再按选定渠道拉起收银台</summary>
    public async Task<CreatePayResultDto> PayOnlineAsync(int settleId,int customerId,string? channelRaw)
    {
        var payId=await _supplement.EnsurePendingSupplementAsync(settleId,customerId);
        if(string.IsNullOrWhiteSpace(channelRaw)) return new CreatePayResultDto{PayId=payId};

        var r=await _supplement.PayAsync(payId,customerId,channelRaw);
        return new CreatePayResultDto
        {
            PayId=payId,
            OrderNo=r.OrderNo,
            PayType=r.PayType,
            Amount=r.Amount,
            PayUrl=r.PayUrl,
            QrCode=r.QrCode,
            PayMode=r.PayMode,
            RealPay=r.RealPay,
        };
    }

    ///<summary>结算核算结果</summary>
    private sealed record SettleCalc(decimal HourlyRent,decimal Multiplier,int ActualHours,decimal ActualRent,int OverdueHours,decimal OverdueFee,decimal RentTotal,decimal RentDiff,decimal Refund,decimal Supplement);

    ///<summary>统一的结算核算：正常租金只算到约定还车时间，超出部分按超时倍数计为逾期费</summary>
    private async Task<SettleCalc> CalculateAsync(RentalOrder order,decimal damageFee)
    {
        var now=DateTime.Now;
        //计费粒度与下单一致：按小时（向上取整，最少 1 小时）
        var(_,hourlyRent,_)=await PricingResolver.ResolveHourlyAsync(_db,order.VehicleId);

        //正常租金只算到约定还车时间（leaseEnd）为止
        var normalEnd=order.LeaseEnd<now?order.LeaseEnd:now;
        var actualHours=PricingResolver.CalcHours(order.LeaseStart,normalEnd);
        var actualRent=Math.Round(actualHours*hourlyRent,2);

        //未逾期时这里就是 0：「不收」也必须有金额，不能只靠一句规则说明带过
        var overdueHours=order.LeaseEnd<now?PricingResolver.CalcHours(order.LeaseEnd,now):0;
        var multiplierCfgs=await _db.SystemConfigs.Where(c=>c.ConfigKey=="overdue_multiplier").ToListAsync();
        var multiplier=ConfigValue.Decimal(multiplierCfgs,"overdue_multiplier",1.5m);
        var overdueFee=Math.Round(overdueHours*hourlyRent*multiplier,2);

        //落库仍拆成 actual_rent/overdue_fee 两列，是为了保留可审计的构成
        var(rentTotal,rentDiff,refund,supplementFee)=SettlementMath.Settle(order.TotalRent,order.Deposit,damageFee,actualRent,overdueFee);

        return new SettleCalc(hourlyRent,multiplier,actualHours,actualRent,overdueHours,overdueFee,rentTotal,rentDiff,refund,supplementFee);
    }

    ///<summary>结算试算</summary>
    public async Task<SettlePreviewDto> Preview(int orderId)
    {
        var order=await _db.RentalOrders.Include(o=>o.Customer).Include(o=>o.Vehicle).FirstOrDefaultAsync(o=>o.OrderId==orderId)??throw new BusinessException("订单不存在");

        var c=await CalculateAsync(order,0m);
        return new SettlePreviewDto
        {
            OrderId=order.OrderId,
            OrderNo=order.OrderNo,
            CustomerName=order.Customer?.FullName??"",
            VehicleNo=order.Vehicle?.VehicleNo??"",
            LeaseStart=order.LeaseStart,
            LeaseEnd=order.LeaseEnd,
            PrepaidRent=order.TotalRent,
            Deposit=order.Deposit,
            HourlyRent=c.HourlyRent,
            Multiplier=c.Multiplier,
            IsOverdue=c.OverdueHours>0,
            OverdueHours=c.OverdueHours,
            OverdueFee=c.OverdueFee,
            ActualRent=c.ActualRent,
            RentTotal=c.RentTotal,
            ActualHours=c.ActualHours,
            RentDiff=c.RentDiff,
            RefundAmount=c.Refund,
            SupplementAmount=c.Supplement
        };
    }

    ///<summary>结算结果：单据+给前台的提示（尤其是退款是否真的原路退回）</summary>
    public sealed record SettleResult(SettleRecord Record,string Message);

    public async Task<SettleResult> Settle(SettleOrderDto dto,int operatorId)
    {
        //结算会碰三样「不能重复发生」的东西：结算单、押金退款、车辆释放
        return await DbConcurrency.RunAsync(()=>SettleOnce(dto,operatorId),_logger,"订单结算",reset: ()=>_db.ChangeTracker.Clear());
    }

    private async Task<SettleResult> SettleOnce(SettleOrderDto dto,int operatorId)
    {
        if(dto.DamageFee<0) throw new BusinessException("损坏费不能为负数");

        //READ COMMITTED：结算事务里只锁「自己读到的那一行」
        await using var tx=await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            //等锁上限压到 5 秒（MySQL 默认 50 秒会让请求线程与连接池一起被拖住）
            await DbRowLock.PrepareAsync(_db);

            //锁内重读：这一句之后的 order 才是「当前真实状态」，事务外读到的都可能已过期
            var order=await DbRowLock.LockOrderAsync(_db,dto.OrderId)??throw new BusinessException("订单不存在");
            if(order.OrderStatus is not("已支付" or "租赁中")) throw new BusinessException($"订单当前状态为「{order.OrderStatus}」，不可结算" +
                                                                               "（可能已被其他同事结算或取消，请刷新后查看）");

            var c=await CalculateAsync(order,dto.DamageFee);
            var now=DateTime.Now;

            //本单成功支付的那条记录
            var paid=await _db.PaymentRecords.Where(p=>p.OrderId==order.OrderId&&(p.PayStatus=="已支付"||p.PayStatus=="部分退款")).OrderByDescending(p=>p.PayId).FirstOrDefaultAsync();

            var settle=SettleRecord.Create(
                (dto.OrderId,operatorId),
                (c.ActualRent,c.OverdueFee,dto.DamageFee),
                (c.Refund,c.Supplement,dto.Remark),
                now);
            _db.SettleRecords.Add(settle);

            //结算记录落库、订单置「已完结」、车辆释放必须同进同退
            order.MarkReturned(now);

            //归还结算本身就是「还车」的最终落点：顾客提交过的还车申请必须在这里一并结清
            if(order.EarlyReturnStatus=="待审核") order.ClearEarlyReturnFlag();

            await _db.SaveChangesAsync();

            //车辆释放走统一闸门
            await VehicleLeaseGuard.TryReleaseAsync(_db,order.VehicleId,order.OrderId);

            //退款必须和结算同进同退：· 支付宝真实通道 → 在这里调网关原路退回
            string note;
            if(c.Supplement>0)
            {
                //SupplementPayMethod 已由前端收敛为「现金/支付宝/微信/银行卡/待顾客选择」之一
                var channel=string.IsNullOrWhiteSpace(dto.SupplementPayMethod)?SupplementPayMethods.Cash:dto.SupplementPayMethod;
                note=channel switch
                {
                    SupplementPayMethods.Cash=>await SettleNoteAsync(order,c,paid),
                    SupplementPayMethods.CustomerChoice=>await _supplement.NotifySupplementChoiceAsync(order,c.Supplement),
                    _=>await _supplement.CreateOnlineSupplementAsync(order,c.Supplement,channel),
                };
            }
            else
            {
                note=await SettleNoteAsync(order,c,paid);
            }

            await tx.CommitAsync();
            return new SettleResult(settle,note);
        }
        catch(DbUpdateException ex) when(DbConcurrency.IsDuplicateKey(ex))
        {
            //唯一索引 uk_settle_order 被触发：这张单已经有结算记录了
            await tx.RollbackAsync();
            _logger.LogWarning(ex,"订单 {OrderId} 重复结算被唯一索引拦截",dto.OrderId);
            throw new BusinessException("该订单已完成结算，请勿重复提交（如确需调整请走财务冲正流程）");
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    ///<summary>生成结算提示文案，并把退款真正执行掉</summary>
    private async Task<string> SettleNoteAsync(RentalOrder order,SettleCalc c,PaymentRecord? paid)
    {
        /*
         * 前台看到的是一句人话，不是对账单：
         *   结算完成 · 已退 ¥985.40 至原支付渠道（支付宝）
         * 通道名、请求号、累计退款额属于财务对账信息，留在结算记录/小票里，
         * 不再往 toast 里塞（原来一句能拖成两行，前台根本没空读）。
         * 但「钱到底动没动」这一条必须保留——真退和账面登记不能糊成同一句话。
         */
        if(c.Refund<=0) return c.Supplement>0?$"结算完成 · 顾客需补缴 ¥{c.Supplement:0.00}，请选择收款方式（现金 / 支付宝 / 微信 / 银行卡）":"结算完成 · 无需退款或补缴";

        if(paid==null)
        {
            await _messages.SendAsync(
                ("customer",order.CustomerId),
                ("refund",order.OrderNo),
                ("结算退款通知",$"订单 {order.OrderNo} 已结算，应退 ¥{c.Refund:0.00}，但未找到有效支付记录，门店将线下为您办理退款，请留意门店通知。"));
            return $"结算完成 · 应退 ¥{c.Refund:0.00}，但无有效支付记录，请联系财务核实";
        }

        var r=await _payment.Refund(paid.PayId,c.Refund,$"订单 {order.OrderNo} 归还结算");

        //向顾客说明退款去向：真实通道=原路退回
        var dest=r.RealMoney?$"将按原支付渠道（{paid.PayType}）原路退回，预计到账 1-3 个工作日，请注意查收。":$"当前为模拟收银台，退款 ¥{c.Refund:0.00} 已作账面登记，请到门店领取现金并留存凭证。";
        await _messages.SendAsync(
            ("customer",order.CustomerId),
            ("refund",order.OrderNo),
            ("结算退款通知",$"订单 {order.OrderNo} 已结算，应退 ¥{c.Refund:0.00}。{dest}"));

        return r.RealMoney?$"结算完成 · 已退 ¥{c.Refund:0.00} 至原支付渠道（{paid.PayType}）":$"结算完成 · 未真实划款，请线下退现金 ¥{c.Refund:0.00}";
    }

    ///<summary>财务对账：按日期范围核对「应收（订单租金+押金）」与「实收（支付记录）」</summary>
    public async Task<object> Reconcile(DateTime startDate,DateTime endDate)
    {
        if(endDate<startDate) throw new BusinessException("结束日期不能早于开始日期");
        var end=endDate.Date.AddDays(1).AddSeconds(-1);//包含结束日全天

        //该时间段内创建的订单（应收口径）
        var orders=await _db.RentalOrders.Where(o=>o.CreateTime>=startDate.Date&&o.CreateTime<=end&&o.OrderStatus!="已取消").ToListAsync();

        //实收口径必须与应收口径对齐到「同一批订单」
        var payments=await _db.PaymentRecords.Where(p=>p.BizType=="order"&&PayStatuses.MoneyArrived.Contains(p.PayStatus)&&_db.RentalOrders.Any(o=>o.OrderId==p.OrderId&&o.CreateTime>=startDate.Date&&o.CreateTime<=end&&o.OrderStatus!="已取消")).ToListAsync();

        //补缴口径：只计已到账的补缴支付
        var paidSupplement=await _db.PaymentRecords.Where(p=>p.BizType==BizTypeSupplement&&p.PayStatus=="已支付"&&_db.RentalOrders.Any(o=>o.OrderId==p.OrderId&&o.CreateTime>=startDate.Date&&o.CreateTime<=end&&o.OrderStatus!="已取消")).SumAsync(p=>p.PayAmount);

        //该时间段内的结算记录
        var settles=await _db.SettleRecords.Where(s=>s.SettleTime>=startDate.Date&&s.SettleTime<=end).ToListAsync();

        var expectedRent=orders.Sum(o=>o.TotalRent);
        var expectedDeposit=orders.Sum(o=>o.Deposit);
        var expectedTotal=expectedRent+expectedDeposit;

        //实收只统计「钱到过账」的支付记录
        var actualReceived=payments.Sum(p=>p.PayAmount);
        var paidCount=payments.Count;

        //退款口径：只认支付记录上实际执行的累计退款（refund_amount）
        var totalRefund=payments.Sum(p=>p.RefundAmount);
        var totalSupplement=paidSupplement;
        var settleCount=settles.Count;

        //净收入=实收-退款+补缴
        var netIncome=Math.Round(actualReceived-totalRefund+totalSupplement,2);
        //对账差异=应收-实收（正数表示少收，负数表示多收）
        var difference=Math.Round(expectedTotal-actualReceived,2);

        //差异文案单独算：三级判断用 switch 表达式，既不嵌套三元（Sonar S3358）
        var remark=difference switch
        {
            _ when Math.Abs(difference) < 0.01m=>"账目平衡",
            > 0=>$"存在 {difference:C} 差额（应收大于实收）",
            _=>$"存在 {-difference:C} 差额（实收大于应收）"
        };

        return new
        {
            StartDate=startDate.Date.ToString("yyyy-MM-dd"),
            EndDate=endDate.Date.ToString("yyyy-MM-dd"),
            OrderCount=orders.Count,
            PaidCount=paidCount,
            SettleCount=settleCount,
            ExpectedRent=Math.Round(expectedRent,2),
            ExpectedDeposit=Math.Round(expectedDeposit,2),
            ExpectedTotal=Math.Round(expectedTotal,2),
            ActualReceived=Math.Round(actualReceived,2),
            TotalRefund=Math.Round(totalRefund,2),
            TotalSupplement=Math.Round(totalSupplement,2),
            NetIncome=netIncome,
            Difference=difference,
            IsBalanced=Math.Abs(difference) < 0.01m,
            Remark=remark
        };
    }
}

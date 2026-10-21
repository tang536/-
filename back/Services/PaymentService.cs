using System.Data;
using ERental.Data;
using ERental.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERental.Services;

public class PaymentService(
    AppDbContext db, AlipayService alipay, PayConfigService payConfig,
    WechatPayService wechat, BankPayService bank, ILogger<PaymentService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly AlipayService _alipay = alipay;
    private readonly PayConfigService _payConfig = payConfig;
    private readonly WechatPayService _wechat = wechat;
    private readonly BankPayService _bank = bank;
    private readonly ILogger<PaymentService> _logger = logger;

    /// <summary>
    /// 支付记录列表。用 AsNoTracking + Select 投影成 DTO：
    /// ① 避免把 RentalOrder → Customer 整棵对象图（含登录账号、身份证号）交给前端；
    /// ② 避免 EF 关系修复回填反向集合形成循环引用（维保模块曾因同类问题返回 500）。
    /// </summary>
    /// <param name="status">按支付状态过滤（如「已支付」）；传 null 表示不过滤</param>
    /// <param name="from">统计起始日（含），财务在报表页自选区间时传入</param>
    /// <param name="to">统计截止日（含当天 23:59:59），右端点 +1 天避免漏掉截止日当天</param>
    public async Task<List<PaymentDto>> GetAll(string? status = null, DateTime? from = null, DateTime? to = null)
    {
        var q = _db.PaymentRecords.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(status)) q = q.Where(p => p.PayStatus == status);

        var rangeFrom = from?.Date;
        var rangeTo = to?.Date.AddDays(1);
        if (rangeFrom.HasValue && rangeTo.HasValue)
            q = q.Where(p => p.PayTime.HasValue && p.PayTime >= rangeFrom && p.PayTime < rangeTo);

        return await q.OrderByDescending(p => p.PayTime)
            .Select(p => new PaymentDto
            {
                PayId = p.PayId,
                OrderId = p.OrderId,
                OrderNo = p.RentalOrder != null ? p.RentalOrder.OrderNo : "",
                CustomerName = p.RentalOrder != null && p.RentalOrder.Customer != null
                    ? p.RentalOrder.Customer.FullName : "",
                VehicleNo = p.RentalOrder != null && p.RentalOrder.Vehicle != null
                    ? p.RentalOrder.Vehicle.VehicleNo : "",
                PayAmount = p.PayAmount,
                PayType = p.PayType ?? "",
                PayStatus = p.PayStatus,
                RefundAmount = p.RefundAmount,
                TransactionNo = p.TransactionNo ?? "",
                PayTime = p.PayTime
            }).ToListAsync();
    }

    /// <summary>退款结果：Message 直接给前台/财务看，RealMoney 表示是否真的走了资金通道</summary>
    public sealed record RefundResult(string Message, bool RealMoney, decimal RefundedTotal, string PayStatus);

    /// <summary>
    /// 退款（需求 4.2.5「剩余押金原路退还」）。
    ///
    /// 三条口径，按通道能力分流，**绝不把"账面退款"伪装成"已原路退回"**：
    ///   ① 真实凭证齐全的通道（支付宝 / 微信 / 银行卡）→ 调该通道的退款接口原路退回，
    ///      成功后按累计金额改 refund_amount；金额未退满为「部分退款」，退满为「已退款」；
    ///   ② 模拟支付模式（通道凭证不全，收款本身就没走资金）→ 只记账，返回文案明确写「未发生资金划转」。
    ///
    /// 幂等键：`{payId}R{累计应退金额的分}`，三通道同一口径——支付宝作 out_request_no、
    /// 微信作 out_refund_no、银行卡作 out_refund_no。同一支付记录、同一累计退款额只会产生一个请求号，
    /// 网关成功但本地事务回滚后重试不会重复退款。
    /// 累计退款金额同时受 `pay_amount - refund_amount` 约束，不存在退超。
    /// </summary>
    /// <param name="payId">支付记录 ID（不是订单 ID）</param>
    /// <param name="amount">本次退款金额（部分退款时传本次金额，不是累计金额）</param>
    /// <param name="reason">退款原因，写进通道退款单，便于对账</param>
    public async Task<RefundResult> Refund(int payId, decimal amount, string? reason = null)
    {
        // 退款有两个入口：财务在支付记录上直接退（独立事务），以及结算流程内部退押金（外层已有事务）。
        // 已有事务就复用它——EF 不支持嵌套 BeginTransaction，硬开一次会直接抛
        // "The connection is already in a transaction"。
        var ownsTx = _db.Database.CurrentTransaction == null;
        IDbContextTransaction? tx = null;
        try
        {
            if (ownsTx)
            {
                tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
                await DbRowLock.PrepareAsync(_db);
            }
            var result = await RefundCoreAsync(payId, amount, reason);
            if (tx != null) await tx.CommitAsync();
            return result;
        }
        catch (DbUpdateException ex) when (DbConcurrency.IsDuplicateKey(ex))
        {
            // 唯一索引/注意点：退款本身不改唯一键，这里兜的是支付记录被并发写入的极端情况
            if (tx != null) await tx.RollbackAsync();
            _logger.LogWarning(ex, "支付记录 {PayId} 退款时发生唯一键冲突", payId);
            throw new Exception("该笔退款正在被其他操作处理，请稍后刷新后重试");
        }
        catch
        {
            if (tx != null) await tx.RollbackAsync();
            throw;
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }
    }

    /// <summary>
    /// 退款主流程（必须在事务内、且已持有支付记录行锁的前提下调用）。
    /// </summary>
    private async Task<RefundResult> RefundCoreAsync(int payId, decimal amount, string? reason)
    {
        // 行锁是这里的关键：并发退同一笔款时，第二个请求阻塞在锁上；
        // 第一个提交后它重读到的 refund_amount 已包含前一笔，于是「可退余额不足」被正确拦下。
        // 没有这把锁的话，两笔各退 ¥100 的请求都会读到 refund_amount=0，累计只记 ¥100，实际却退了 ¥200。
        var p = await DbRowLock.LockPaymentAsync(_db, payId)
                ?? throw new Exception("支付记录不存在");

        if (p.PayStatus is "待支付" or "已取消")
            throw new Exception("该笔支付尚未成功，不能退款");
        if (amount <= 0) throw new Exception("退款金额必须大于 0");
        if (p.RefundAmount > p.PayAmount) throw new Exception("支付记录金额异常，请联系管理员核账");

        var refundable = Math.Round(p.PayAmount - p.RefundAmount, 2);
        if (refundable <= 0) throw new Exception("该笔支付已全额退款，不能重复退款");
        if (amount > refundable)
            throw new Exception($"退款金额不能超过可退金额 ¥{refundable:0.00}");

        var newTotal = Math.Round(p.RefundAmount + amount, 2);
        // 幂等键：通道无关，只由「支付记录 + 累计应退额」决定（规则见 RefundKey）
        var requestNo = RefundKey.For(p.PayId, newTotal);
        var payType = p.PayType ?? "";

        // ① 支付宝真实通道：必须走网关
        if (string.Equals(payType, "支付宝", StringComparison.Ordinal))
        {
            var rt = await _payConfig.ResolveAsync();
            if (rt.IsReal && rt.CredentialsComplete)
            {
                if (string.IsNullOrWhiteSpace(p.TransactionNo))
                    throw new Exception("该笔支付宝支付缺少交易号，无法原路退款；请到支付宝商户后台按订单号手工退款。");

                var (ok, msg) = await _alipay.RefundAsync(p.TransactionNo!, requestNo, amount, reason);
                if (!ok) throw new Exception(msg);

                return await ApplyRefundedAsync(p, newTotal, amount, "支付宝", requestNo);
            }
        }

        // ② 微信真实通道：APIv3 退款，金额单位为分（服务内部换算）
        if (string.Equals(payType, "微信", StringComparison.Ordinal))
        {
            var (_, real) = await _wechat.ResolveAsync();
            if (real)
            {
                var tradeNo = string.IsNullOrWhiteSpace(p.TransactionNo)
                    ? await OrderNoOfAsync(p.OrderId)
                    : p.TransactionNo!;
                var (ok, msg) = await _wechat.RefundAsync(tradeNo, requestNo, amount, p.PayAmount, reason);
                if (!ok) throw new Exception(msg);

                return await ApplyRefundedAsync(p, newTotal, amount, "微信", requestNo);
            }
        }

        // ③ 银行卡真实通道：通用收银网关退款（按同一套 HMAC 签名）
        if (string.Equals(payType, "银行卡", StringComparison.Ordinal))
        {
            var (_, real) = await _bank.ResolveAsync();
            if (real)
            {
                var tradeNo = await OrderNoOfAsync(p.OrderId);
                if (string.IsNullOrWhiteSpace(tradeNo) && string.IsNullOrWhiteSpace(p.TransactionNo))
                    throw new Exception("该笔银行卡支付缺少订单号与流水号，无法原路退款；请在收单机构后台手工退款。");

                var (ok, msg) = await _bank.RefundAsync(
                    string.IsNullOrWhiteSpace(tradeNo) ? p.TransactionNo! : tradeNo,
                    requestNo, amount, p.PayAmount, reason);
                if (!ok) throw new Exception(msg);

                return await ApplyRefundedAsync(p, newTotal, amount, "银行卡", requestNo);
            }
        }

        // ④ 模拟 / 凭证不全：只记账，如实告知
        p.RefundAmount = newTotal;
        p.PayStatus = newTotal >= p.PayAmount ? "已退款" : "部分退款";
        await _db.SaveChangesAsync();

        var why = string.IsNullOrEmpty(payType) || payType == "支付宝"
            ? "当前为模拟支付模式，没有真实资金流"
            : $"{payType}通道未配置真实凭证（收款走的是模拟收银台）";
        return new RefundResult(
            $"已完成账面退款 ¥{amount:0.00}（累计 ¥{newTotal:0.00}）；{why}，请线下退还现金并留存凭证。",
            false, newTotal, p.PayStatus);
    }

    /// <summary>退款成功后统一落库：写累计金额、按是否退满改状态，返回给前端的文案带上通道名与请求号</summary>
    private async Task<RefundResult> ApplyRefundedAsync(
        Models.PaymentRecord p, decimal newTotal, decimal amount, string channel, string requestNo)
    {
        p.RefundAmount = newTotal;
        p.PayStatus = newTotal >= p.PayAmount ? "已退款" : "部分退款";
        await _db.SaveChangesAsync();
        return new RefundResult(
            $"{channel}原路退款 ¥{amount:0.00} 已受理（累计 ¥{newTotal:0.00}，请求号 {requestNo}）",
            true, newTotal, p.PayStatus);
    }

    /// <summary>取订单号：微信/银行卡退款都支持按商户订单号定位原交易，不必依赖交易号</summary>
    private async Task<string> OrderNoOfAsync(int orderId) =>
        await _db.RentalOrders.Where(o => o.OrderId == orderId)
            .Select(o => o.OrderNo).FirstOrDefaultAsync() ?? string.Empty;
}

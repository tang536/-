using System.Data;
using ERental.Data;
using ERental.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERental.Services;

public class PaymentService(AppDbContext db, AlipayService alipay, PayConfigService payConfig, WechatPayService wechat, BankPayService bank, ILogger<PaymentService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly AlipayService _alipay = alipay;
    private readonly PayConfigService _payConfig = payConfig;
    private readonly WechatPayService _wechat = wechat;
    private readonly BankPayService _bank = bank;
    private readonly ILogger<PaymentService> _logger = logger;

    ///<summary>支付记录列表</summary>
    public async Task<List<PaymentDto>> GetAll(string? status = null, DateTime? from = null, DateTime? to = null)
    {
        var q = _db.PaymentRecords.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(status)) q = q.Where(p => p.PayStatus == status);

        var rangeFrom = from?.Date;
        var rangeTo = to?.Date.AddDays(1);
        if (rangeFrom.HasValue && rangeTo.HasValue) q = q.Where(p => p.PayTime.HasValue && p.PayTime >= rangeFrom && p.PayTime < rangeTo);

        return await q.OrderByDescending(p => p.PayTime).Select(p => new PaymentDto
        {
            PayId = p.PayId,
            OrderId = p.OrderId,
            OrderNo = p.RentalOrder != null ? p.RentalOrder.OrderNo : "",
            CustomerName = p.RentalOrder != null && p.RentalOrder.Customer != null ? p.RentalOrder.Customer.FullName : "",
            VehicleNo = p.RentalOrder != null && p.RentalOrder.Vehicle != null ? p.RentalOrder.Vehicle.VehicleNo : "",
            PayAmount = p.PayAmount,
            PayType = p.PayType ?? "",
            PayStatus = p.PayStatus,
            RefundAmount = p.RefundAmount,
            TransactionNo = p.TransactionNo ?? "",
            PayTime = p.PayTime
        }).ToListAsync();
    }

    ///<summary>退款结果：Message 直接给前台/财务看，RealMoney 表示是否真的走了资金通道</summary>
    public sealed record RefundResult(string Message, bool RealMoney, decimal RefundedTotal, string PayStatus);

    public async Task<RefundResult> Refund(int payId, decimal amount, string? reason = null)
    {
        //退款有两个入口：财务在支付记录上直接退（独立事务），以及结算流程内部退押金（外层已有事务）
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
            //唯一索引/注意点：退款本身不改唯一键，这里兜的是支付记录被并发写入的极端情况
            if (tx != null) await tx.RollbackAsync();
            _logger.LogWarning(ex, "支付记录 {PayId} 退款时发生唯一键冲突", payId);
            throw new BusinessException("该笔退款正在被其他操作处理，请稍后刷新后重试");
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

    ///<summary>退款主流程（必须在事务内、且已持有支付记录行锁的前提下调用）</summary>
    private async Task<RefundResult> RefundCoreAsync(int payId, decimal amount, string? reason)
    {
        //行锁是这里的关键：并发退同一笔款时，第二个请求阻塞在锁上
        var p = await DbRowLock.LockPaymentAsync(_db, payId) ?? throw new BusinessException("支付记录不存在");

        if (p.PayStatus is "待支付" or "已取消") throw new BusinessException("该笔支付尚未成功，不能退款");
        if (amount <= 0) throw new BusinessException("退款金额必须大于 0");
        if (p.RefundAmount > p.PayAmount) throw new BusinessException("支付记录金额异常，请联系管理员核账");

        var refundable = Math.Round(p.PayAmount - p.RefundAmount, 2);
        if (refundable <= 0) throw new BusinessException("该笔支付已全额退款，不能重复退款");
        if (amount > refundable) throw new BusinessException($"退款金额不能超过可退金额 ¥{refundable:0.00}");

        var newTotal = Math.Round(p.RefundAmount + amount, 2);
        //幂等键：通道无关，只由「支付记录+累计应退额」决定（规则见 RefundKey）
        var requestNo = RefundKey.For(p.PayId, newTotal);
        var payType = p.PayType ?? "";

        //商户订单号（out_trade_no）：首付款沿用租赁订单号
        var merchantOutTradeNo = !string.IsNullOrWhiteSpace(p.OutTradeNo) ? p.OutTradeNo : await OrderNoOfAsync(p.OrderId);

        //① 支付宝真实通道：必须走网关
        var viaAlipay = await TryRefundByAlipayAsync(p, payType, merchantOutTradeNo, requestNo, amount, reason, newTotal);
        if (viaAlipay != null) return viaAlipay;

        //② 微信真实通道：APIv3 退款，金额单位为分（服务内部换算）
        var viaWechat = await TryRefundByWechatAsync(p, payType, merchantOutTradeNo, requestNo, amount, reason, newTotal);
        if (viaWechat != null) return viaWechat;

        //③ 银行卡真实通道：通用收银网关退款（按同一套 HMAC 签名）
        var viaBank = await TryRefundByBankAsync(p, payType, merchantOutTradeNo, requestNo, amount, reason, newTotal);
        if (viaBank != null) return viaBank;

        //④ 模拟/凭证不全：未进入任何真实通道，只记账、如实告知
        var why = string.IsNullOrEmpty(payType) || payType == "支付宝" ? "当前为模拟支付模式，没有真实资金流" : $"{payType}通道未配置真实凭证（收款走的是模拟收银台）";
        return await LedgerRefundAsync(p, newTotal, amount, payType, why);
    }

    ///<summary>支付宝真实通道退款</summary>
    private async Task<RefundResult?> TryRefundByAlipayAsync(Models.PaymentRecord p, string payType, string merchantOutTradeNo,
        string requestNo, decimal amount, string? reason, decimal newTotal)
    {
        if (!string.Equals(payType, "支付宝", StringComparison.Ordinal)) return null;

        var rt = await _payConfig.ResolveAsync();
        if (!rt.IsReal || !rt.CredentialsComplete) return null;

        if (string.IsNullOrWhiteSpace(merchantOutTradeNo)) throw new BusinessException("该笔支付宝支付缺少商户订单号，无法原路退款；请到支付宝商户后台按交易号手工退款。");

        var (ok, msg) = await _alipay.RefundAsync(merchantOutTradeNo, p.TransactionNo, requestNo, amount, reason);
        if (ok) return await ApplyRefundedAsync(p, newTotal, amount, "支付宝", requestNo);
        if (IsTradeNotFound(msg)) return await LedgerRefundAsync(p, newTotal, amount, "支付宝", "该笔支付宝收款在网关侧未找到对应交易（可能为模拟支付或订单号不匹配）");
        throw new BusinessException(msg);
    }

    ///<summary>微信真实通道退款</summary>
    private async Task<RefundResult?> TryRefundByWechatAsync(Models.PaymentRecord p, string payType, string merchantOutTradeNo,
        string requestNo, decimal amount, string? reason, decimal newTotal)
    {
        if (!string.Equals(payType, "微信", StringComparison.Ordinal)) return null;

        var (_, real) = await _wechat.ResolveAsync();
        if (!real) return null;

        if (string.IsNullOrWhiteSpace(merchantOutTradeNo)) throw new BusinessException("该笔微信支付缺少商户订单号，无法原路退款；请到微信商户平台按订单号手工退款。");

        var (ok, msg) = await _wechat.RefundAsync(merchantOutTradeNo, requestNo, amount, p.PayAmount, reason);
        if (ok) return await ApplyRefundedAsync(p, newTotal, amount, "微信", requestNo);
        if (IsTradeNotFound(msg)) return await LedgerRefundAsync(p, newTotal, amount, "微信", "该笔微信收款在网关侧未找到对应交易（可能为模拟支付或订单号不匹配）");
        throw new BusinessException(msg);
    }

    ///<summary>银行卡真实通道退款</summary>
    private async Task<RefundResult?> TryRefundByBankAsync(Models.PaymentRecord p, string payType, string merchantOutTradeNo,
        string requestNo, decimal amount, string? reason, decimal newTotal)
    {
        if (!string.Equals(payType, "银行卡", StringComparison.Ordinal)) return null;

        var (_, real) = await _bank.ResolveAsync();
        if (!real) return null;

        if (string.IsNullOrWhiteSpace(merchantOutTradeNo)) throw new BusinessException("该笔银行卡支付缺少商户订单号，无法原路退款；请在收单机构后台手工退款。");

        var (ok, msg) = await _bank.RefundAsync(merchantOutTradeNo, requestNo, amount, p.PayAmount, reason);
        if (ok) return await ApplyRefundedAsync(p, newTotal, amount, "银行卡", requestNo);
        if (IsTradeNotFound(msg)) return await LedgerRefundAsync(p, newTotal, amount, "银行卡", "该笔银行卡收款在网关侧未找到对应交易（可能为模拟支付或订单号不匹配）");
        throw new BusinessException(msg);
    }

    ///<summary>网关侧「交易不存在」类错误的识别：覆盖支付宝/微信/银行卡的中英文表述</summary>
    private static bool IsTradeNotFound(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return false;
        return msg.Contains("不存在") || msg.Contains("未找到")
            || msg.Contains("not exist", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("not_found", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("TRADE_NOT_EXIST", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("ORDER_NOT_EXIST", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("NO_SUCH", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("RESOURCE_NOT_EXISTS", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("INVALID_TRANSACTION", StringComparison.OrdinalIgnoreCase);
    }

    ///<summary>账面退款（无真实资金流）：写累计退款额、按是否退满改状态，返回如实告知的文案</summary>
    private async Task<RefundResult> LedgerRefundAsync(Models.PaymentRecord p, decimal newTotal, decimal amount, string channel, string cause)
    {
        p.ApplyRefund(newTotal);
        await _db.SaveChangesAsync();
        var name = string.IsNullOrWhiteSpace(channel) ? "支付宝" : channel;
        return new RefundResult($"{cause}，已对{name}作账面退款 ¥{amount:0.00}（累计 ¥{newTotal:0.00}）；未发生真实资金划转，请线下退还现金并留存凭证。", false, newTotal, p.PayStatus);
    }

    ///<summary>退款成功后统一落库：写累计金额、按是否退满改状态，返回给前端的文案带上通道名与请求号</summary>
    private async Task<RefundResult> ApplyRefundedAsync(Models.PaymentRecord p, decimal newTotal, decimal amount, string channel, string requestNo)
    {
        p.ApplyRefund(newTotal);
        await _db.SaveChangesAsync();
        return new RefundResult($"{channel}原路退款 ¥{amount:0.00} 已受理（累计 ¥{newTotal:0.00}，请求号 {requestNo}）", true, newTotal, p.PayStatus);
    }

    ///<summary>取订单号：微信/银行卡退款都支持按商户订单号定位原交易，不必依赖交易号</summary>
    private async Task<string> OrderNoOfAsync(int orderId) => await _db.RentalOrders.Where(o => o.OrderId == orderId).Select(o => o.OrderNo).FirstOrDefaultAsync() ?? string.Empty;

    //支付状态查询原先在 PaymentController 里直接注入 AppDbContext 拼查询、改实体、SaveChanges

    ///<summary>按订单号取订单（保留跟踪，主动查单后需要 Reload 看到最新状态）</summary>
    public async Task<Models.RentalOrder?> FindOrderAsync(string orderNo) =>
        await _db.RentalOrders.FirstOrDefaultAsync(o => o.OrderNo == orderNo);

    ///<summary>按商户交易号定位补缴单主键</summary>
    public async Task<int?> FindSupplementPayIdAsync(string outTradeNo) =>
        await _db.PaymentRecords.AsNoTracking()
            .Where(p => p.OutTradeNo == outTradeNo && p.BizType == "supplement")
            .Select(p => (int?)p.PayId)
            .FirstOrDefaultAsync();

    ///<summary>本单最近一次选择的支付渠道（空串表示顾客还没点过「去支付」）</summary>
    public async Task<string> LatestPayTypeAsync(int orderId) =>
        await _db.PaymentRecords.AsNoTracking()
            .Where(p => p.OrderId == orderId)
            .OrderByDescending(p => p.PayId)
            .Select(p => p.PayType ?? "")
            .FirstOrDefaultAsync() ?? "";

    ///<summary>主动查单会当场幂等入账，轮询侧必须重载实体</summary>
    public async Task ReloadAsync(Models.RentalOrder order) => await _db.Entry(order).ReloadAsync();

    ///<summary>本单最近一笔「钱到过账」的支付记录（含部分退款/已退款，结算后交易号才不丢）</summary>
    public async Task<Models.PaymentRecord?> LatestArrivedAsync(int orderId) =>
        await _db.PaymentRecords.AsNoTracking()
            .Where(p => p.OrderId == orderId && PayStatuses.MoneyArrived.Contains(p.PayStatus))
            .OrderByDescending(p => p.PayId)
            .FirstOrDefaultAsync();

    ///<summary>按支付记录主键取数（只读）</summary>
    public async Task<Models.PaymentRecord?> FindPayRecordAsync(int payId, string? bizType = null)
    {
        var q = _db.PaymentRecords.AsNoTracking().Where(p => p.PayId == payId);
        if (!string.IsNullOrWhiteSpace(bizType)) q = q.Where(p => p.BizType == bizType);
        return await q.FirstOrDefaultAsync();
    }

    ///<summary>订单是否属于该顾客：补缴单越权校验用，不信任前端传来的 customerId</summary>
    public async Task<bool> IsOrderOwnedByAsync(int orderId, int customerId) =>
        await _db.RentalOrders.AnyAsync(o => o.OrderId == orderId && o.CustomerId == customerId);

    ///<summary>发起支付时把「待定」占位渠道改成顾客本次所选，一次落库完成</summary>
    public async Task SetPendingChannelAsync(int orderId, string payType)
    {
        var pending = await _db.PaymentRecords
            .Where(p => p.OrderId == orderId && p.PayStatus == "待支付")
            .ToListAsync();
        if (pending.Count == 0) return;
        foreach (var record in pending) record.SetChannel(payType);
        await _db.SaveChangesAsync();
    }
}

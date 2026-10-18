using ERental.Data;
using ERental.DTOs;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class PaymentService(AppDbContext db, AlipayService alipay, PayConfigService payConfig)
{
    private readonly AppDbContext _db = db;
    private readonly AlipayService _alipay = alipay;
    private readonly PayConfigService _payConfig = payConfig;

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
    ///   ① 支付宝 + 真实凭证 → 调 <see cref="AlipayService.RefundAsync"/> 原路退回，
    ///      成功后按累计金额改 refund_amount；金额未退满为「部分退款」，退满为「已退款」；
    ///   ② 模拟支付模式 → 没有真实资金流，只记账，返回文案明确写「未发生资金划转」；
    ///   ③ 微信 / 银行卡 → 这两个通道的退款接口尚未接入（收款侧也是模拟），
    ///      同样只记账并如实告知，避免财务以为钱已经退回去了。
    ///
    /// 幂等键：`{payId}R{累计应退金额的分}`。同一支付记录、同一累计退款额只会产生一个
    /// out_request_no——网关成功但本地事务回滚后重试不会重复退款。
    /// 累计退款金额同时受 `pay_amount - refund_amount` 约束，不存在退超。
    /// </summary>
    /// <param name="payId">支付记录 ID（不是订单 ID）</param>
    /// <param name="amount">本次退款金额（部分退款时传本次金额，不是累计金额）</param>
    /// <param name="reason">退款原因，写进支付宝退款单，便于对账</param>
    public async Task<RefundResult> Refund(int payId, decimal amount, string? reason = null)
    {
        var p = await _db.PaymentRecords.FindAsync(payId)
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
        var isAlipay = string.Equals(p.PayType, "支付宝", StringComparison.Ordinal);
        var rt = await _payConfig.ResolveAsync();

        // ① 支付宝真实通道：必须走网关
        if (isAlipay && rt.IsReal && rt.CredentialsComplete)
        {
            if (string.IsNullOrWhiteSpace(p.TransactionNo))
                throw new Exception("该笔支付宝支付缺少交易号，无法原路退款；请到支付宝商户后台按订单号手工退款。");

            var requestNo = $"{p.PayId}R{(int)Math.Round(newTotal * 100)}";
            var (ok, msg) = await _alipay.RefundAsync(p.TransactionNo!, requestNo, amount, reason);
            if (!ok) throw new Exception(msg);

            p.RefundAmount = newTotal;
            p.PayStatus = newTotal >= p.PayAmount ? "已退款" : "部分退款";
            await _db.SaveChangesAsync();
            return new RefundResult(
                $"支付宝原路退款 ¥{amount:0.00} 已受理（累计 ¥{newTotal:0.00}，请求号 {requestNo}）",
                true, newTotal, p.PayStatus);
        }

        // ②③ 模拟 / 微信 / 银行卡：只记账，如实告知
        p.RefundAmount = newTotal;
        p.PayStatus = newTotal >= p.PayAmount ? "已退款" : "部分退款";
        await _db.SaveChangesAsync();

        var why = isAlipay
            ? "当前为模拟支付模式，没有真实资金流"
            : $"{p.PayType}通道的退款接口尚未接入";
        return new RefundResult(
            $"已完成账面退款 ¥{amount:0.00}（累计 ¥{newTotal:0.00}）；{why}，请线下退还现金并留存凭证。",
            false, newTotal, p.PayStatus);
    }
}

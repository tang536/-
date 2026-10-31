using System.Data;
using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>补缴（逾期 / 损坏费用）统一服务</summary>
public class SupplementService(
    AppDbContext db, MessageService messages,
    AlipayService alipay, WechatPayService wechat, BankPayService bank, PayConfigService payConfig,
    IHttpContextAccessor http, ILogger<SupplementService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly MessageService _messages = messages;
    private readonly AlipayService _alipay = alipay;
    private readonly WechatPayService _wechat = wechat;
    private readonly BankPayService _bank = bank;
    private readonly PayConfigService _payConfig = payConfig;
    private readonly IHttpContextAccessor _http = http;
    private readonly ILogger<SupplementService> _logger = logger;

    // ───────────────────────── 顾客自助端 ─────────────────────────

    /// <summary>为某张待支付补缴单发起支付，复用支付宝/微信/银行卡三通道，返回收银台地址或二维码</summary>
    public async Task<CreatePayResultDto> PayAsync(int payId, int userId, string? payTypeRaw)
    {
        var rec = await _db.PaymentRecords.FirstOrDefaultAsync(p => p.PayId == payId && p.BizType == "supplement")
                  ?? throw new Exception("补缴单不存在");
        var order = await _db.RentalOrders.FirstOrDefaultAsync(o => o.OrderId == rec.OrderId)
                    ?? throw new Exception("关联订单不存在");
        if (order.CustomerId != userId) throw new Exception("无权操作该补缴单");
        if (rec.PayStatus != "待支付") throw new Exception("该补缴单已支付，无需重复支付");

        var payType = string.IsNullOrWhiteSpace(payTypeRaw) ? "支付宝" : payTypeRaw!.Trim();
        // 顾客选定渠道后写入记录（付款完成前门店侧也能看到真实意向）
        rec.SetChannel(payType);
        await _db.SaveChangesAsync();

        var rt = await _payConfig.ResolveAsync();
        var real = rt is { IsReal: true, CredentialsComplete: true };

        string payUrl = "", qrCode = "", payMode = "";
        bool realPay;
        switch (payType)
        {
            case "支付宝":
                payUrl = await _alipay.CreatePagePayAsync(
                    rec.OutTradeNo!, $"电动车租赁补缴 {order.OrderNo}", rec.PayAmount,
                    await BuildReturnUrl(),
                    await _payConfig.BuildNotifyUrlAsync(ClientOrigin, "api/payment/alipay/notify"));
                realPay = real;
                break;
            case "微信":
            {
                var wx = await _wechat.CreatePayAsync(rec.OutTradeNo!, $"电动车租赁补缴 {order.OrderNo}", rec.PayAmount, await BuildReturnUrl());
                payUrl = wx.PayUrl;
                qrCode = wx.QrCode;
                payMode = wx.PayMode;
                realPay = wx.Real;
                break;
            }
            case "银行卡":
                (payUrl, realPay) = await _bank.CreatePayAsync(
                    rec.OutTradeNo!, $"电动车租赁补缴 {order.OrderNo}", rec.PayAmount);
                break;
            default:
                throw new Exception($"不支持的支付方式「{payType}」");
        }

        return new CreatePayResultDto
        {
            PayId = rec.PayId,
            OrderNo = order.OrderNo,
            PayType = payType,
            Amount = rec.PayAmount,
            PayUrl = payUrl,
            QrCode = qrCode,
            PayMode = payMode,
            RealPay = realPay
        };
    }

    /// <summary>顾客查询自己的补缴单列表（含待支付/已支付），用于「我的订单 → 待补缴」展示</summary>
    public async Task<object> GetMineAsync(int userId)
    {
        var items = await _db.PaymentRecords
            .Where(p => p.BizType == "supplement"
                        && _db.RentalOrders.Any(o => o.OrderId == p.OrderId && o.CustomerId == userId))
            .OrderByDescending(p => p.PayId)
            .Select(p => new
            {
                p.PayId,
                p.OrderId,
                OrderNo = _db.RentalOrders.Where(o => o.OrderId == p.OrderId).Select(o => o.OrderNo).FirstOrDefault() ?? "",
                p.PayAmount,
                p.PayType,
                p.PayStatus,
                p.PayTime,
                p.TransactionNo
            })
            .ToListAsync();
        return items;
    }

    /// <summary>查询某张补缴单支付状态：若仍为待支付，按所选通道主动查单兜底（应对回调延迟/丢包）</summary>
    public async Task<object> GetStatusAsync(int payId, int userId)
    {
        var rec = await _db.PaymentRecords.FirstOrDefaultAsync(p => p.PayId == payId && p.BizType == "supplement")
                  ?? throw new Exception("补缴单不存在");
        var order = await _db.RentalOrders.FirstOrDefaultAsync(o => o.OrderId == rec.OrderId)
                    ?? throw new Exception("关联订单不存在");
        if (order.CustomerId != userId) throw new Exception("无权查看该补缴单");

        if (rec.PayStatus == "待支付")
        {
            var type = rec.PayType ?? "支付宝";
            try
            {
                if (type == "微信") await _wechat.QueryTradeStatusAsync(rec.OutTradeNo!);
                else if (type == "银行卡") await _bank.QueryTradeStatusAsync(rec.OutTradeNo!);
                else await _alipay.QueryTradeStatusAsync(rec.OutTradeNo!);
                await _db.Entry(rec).ReloadAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "补缴单 {PayId} 查单兜底失败，回退本地状态", payId);
            }
        }

        return new
        {
            rec.PayId,
            order.OrderNo,
            rec.PayAmount,
            rec.PayType,
            rec.PayStatus,
            Paid = rec.PayStatus == "已支付"
        };
    }

    // ───────────────────────── 门店收银端 ─────────────────────────

    /// <summary>待收款补缴账单（前台入口）</summary>
    public async Task<List<SupplementBillDto>> GetBillsAsync(bool onlyOutstanding = true)
    {
        var settles = await _db.SettleRecords
            .Where(s => s.SupplementAmount > 0)
            .Include(s => s.RentalOrder).ThenInclude(o => o!.Customer)
            .Include(s => s.RentalOrder).ThenInclude(o => o!.Vehicle)
            .OrderByDescending(s => s.SettleTime)
            .ToListAsync();

        if (settles.Count == 0) return new List<SupplementBillDto>();

        // 一次性把相关的补缴支付记录捞出来，避免每行一次往返
        var orderIds = settles.Select(s => s.OrderId).Distinct().ToList();
        var pays = await _db.PaymentRecords
            .Where(p => orderIds.Contains(p.OrderId) && p.BizType == "supplement")
            .OrderBy(p => p.PayId)
            .ToListAsync();

        var bills = settles.Select(s => BuildBill(s, pays.Where(p => p.OrderId == s.OrderId).ToList())).ToList();
        return onlyOutstanding ? bills.Where(b => !b.Collected).ToList() : bills;
    }

    /// <summary>补缴收款（前台收银台）</summary>
    public async Task<SupplementCollectResultDto> CollectAsync(int settleId, string channelRaw)
    {
        var channel = NormalizeChannel(channelRaw);
        if (channel is "线上" or "" or null)
            throw new Exception("请选择收款方式（现金 / 支付宝 / 微信 / 银行卡）");

        return channel == "现金"
            ? await CollectCashAsync(settleId)
            : await CollectOnlineAsync(settleId, channel);
    }

    /// <summary>查询补缴收款状态（前台轮询用）</summary>
    public async Task<SupplementBillDto> GetStatusBySettleAsync(int settleId)
    {
        var bill = await BuildBillAsync(settleId);
        if (bill.Collected || bill.PendingPayId == null) return bill;

        var pending = await _db.PaymentRecords.FirstOrDefaultAsync(p => p.PayId == bill.PendingPayId);
        if (pending?.OutTradeNo == null) return bill;

        try
        {
            if (pending.PayType == "微信") await _wechat.QueryTradeStatusAsync(pending.OutTradeNo);
            else if (pending.PayType == "支付宝") await _alipay.QueryTradeStatusAsync(pending.OutTradeNo);
            else if (pending.PayType == "银行卡") await _bank.QueryTradeStatusAsync(pending.OutTradeNo);
            else return bill; // 现金等人工渠道：无待支付单可查
        }
        catch (Exception ex)
        {
            // 查单失败不能影响前台看到本地状态：回退到库里的记录继续展示
            _logger.LogWarning(ex, "补缴单 {PayId} 查单兜底失败，回退本地状态", bill.PendingPayId);
            return bill;
        }

        return await BuildBillAsync(settleId);
    }

    /// <summary>催缴：顾客关了支付页就找不到人，前台需要有「再提醒一次」的入口</summary>
    public async Task<decimal> RemindAsync(int settleId)
    {
        var bill = await BuildBillAsync(settleId);
        if (bill.Amount <= 0) throw new Exception("该结算单没有补缴款，无需催缴");
        if (bill.Collected) throw new Exception("该结算单的补缴款已收清，无需再催缴");

        var order = await _db.RentalOrders
            .Where(o => o.OrderId == bill.OrderId)
            .Select(o => new { o.OrderNo, o.CustomerId })
            .FirstOrDefaultAsync() ?? throw new Exception("结算记录未关联租赁订单");

        var online = bill.PendingPayId != null;
        await _messages.SendAsync(
            "customer", order.CustomerId, "supplement", order.OrderNo, "补缴催缴提醒",
            $"门店提醒：订单 {order.OrderNo} 尚有 ¥{bill.Amount - bill.PaidAmount:0.00} 未缴清（逾期/损坏费用）。" +
            (online
                ? "请登录顾客端「我的订单 → 待补缴费用」在线支付，或到店缴纳。"
                : "请到店缴纳，或联系门店获取在线支付链接。"));

        return bill.Amount - bill.PaidAmount;
    }

    // ───────────── 供 SettlementService.Settle 调用（结算时生成线上补缴单） ─────────────

    /// <summary>结算产生补缴、但把「怎么付」交给顾客自己定（线下现金 / 在线支付）</summary>
    public async Task<string> NotifySupplementChoiceAsync(RentalOrder order, decimal amount)
    {
        await _messages.SendAsync(
            "customer", order.CustomerId, "supplement", order.OrderNo, "结算单已生成",
            $"订单 {order.OrderNo} 已完成归还结算，尚有余款 ¥{amount:0.00} 需补缴（逾期/损坏费用）。" +
            "您可在顾客端「我的订单 → 结算单」选择在线支付，或直接到店缴纳现金。");

        return $"结算完成 · 顾客需补缴 ¥{amount:0.00}，可在顾客端在线支付或到店缴纳";
    }

    /// <summary>顾客选择在线支付时，确保这张结算单有一张可付的待支付补缴单： 已有则复用（同一笔钱不挂两张单），金额与剩余应收不一致…</summary>
    public async Task<int> EnsurePendingSupplementAsync(int settleId, int customerId)
    {
        var settle = await _db.SettleRecords
            .Include(s => s.RentalOrder)
            .FirstOrDefaultAsync(s => s.SettleId == settleId)
            ?? throw new Exception("结算单不存在");
        var order = settle.RentalOrder ?? throw new Exception("结算单未关联租赁订单");
        if (order.CustomerId != customerId) throw new Exception("无权操作该结算单");
        if (settle.SupplementAmount <= 0) throw new Exception("该结算单没有需要补缴的款项");

        var pays = await _db.PaymentRecords
            .Where(p => p.OrderId == settle.OrderId && p.BizType == "supplement")
            .OrderBy(p => p.PayId)
            .ToListAsync();

        var paid = pays.Where(p => p.PayStatus == "已支付").Sum(p => p.PayAmount);
        var remaining = Math.Round(settle.SupplementAmount - paid, 2);
        if (remaining <= 0.01m) throw new Exception("该结算单的补缴款已缴清，无需重复支付");

        var pending = pays.FirstOrDefault(p => p.PayStatus == "待支付");
        if (pending == null)
        {
            pending = PaymentRecord.Create(settle.OrderId, remaining, null, "supplement",
                BuildSupplementOutTradeNo(order.OrderNo));
            _db.PaymentRecords.Add(pending);
        }
        else if (pending.PayAmount != remaining)
        {
            pending.PreparePayment(pending.PayType ?? "支付宝", remaining);
        }
        await _db.SaveChangesAsync();
        return pending.PayId;
    }

    /// <summary>生成「线上补缴单」：为逾期/损坏产生的补缴金额创建一条独立的待支付支付记录 （biz_type='supplemen…</summary>
    public async Task<string> CreateOnlineSupplementAsync(RentalOrder order, decimal amount, string channel)
    {
        var outTradeNo = BuildSupplementOutTradeNo(order.OrderNo);
        _db.PaymentRecords.Add(PaymentRecord.Create(order.OrderId, amount,
            channel == "线上" ? null : channel, "supplement", outTradeNo));
        await _db.SaveChangesAsync();

        await _messages.SendAsync(
            "customer", order.CustomerId, "supplement", order.OrderNo, "逾期补缴通知",
            $"订单 {order.OrderNo} 已结算，您尚有余款 ¥{amount:0.00} 待补缴（逾期/损坏费用）。" +
            "请登录顾客端「我的订单 → 待补缴」在线支付，或到店缴纳；逾期可能影响后续租车。");

        return $"结算完成；已生成线上补缴单 ¥{amount:0.00} 并通知顾客在线支付（顾客回到顾客端即可缴款）。";
    }

    // ───────────────────────────── 内部辅助 ─────────────────────────────

    /// <summary>单张结算单的补缴账单（收款弹窗、状态轮询、催缴共用）</summary>
    public async Task<SupplementBillDto> BuildBillAsync(int settleId)
    {
        var settle = await _db.SettleRecords
            .Include(s => s.RentalOrder).ThenInclude(o => o!.Customer)
            .Include(s => s.RentalOrder).ThenInclude(o => o!.Vehicle)
            .FirstOrDefaultAsync(s => s.SettleId == settleId)
            ?? throw new Exception("结算记录不存在");

        var pays = await _db.PaymentRecords
            .Where(p => p.OrderId == settle.OrderId && p.BizType == "supplement")
            .OrderBy(p => p.PayId)
            .ToListAsync();

        return BuildBill(settle, pays);
    }

    private static SupplementBillDto BuildBill(SettleRecord s, List<PaymentRecord> pays)
    {
        var paid = pays.Where(p => p.PayStatus == "已支付").ToList();
        var paidAmount = paid.Sum(p => p.PayAmount);
        var pending = pays.Where(p => p.PayStatus == "待支付").OrderByDescending(p => p.PayId).FirstOrDefault();
        var order = s.RentalOrder;

        // 逾期小时数：结算单只存了逾期费金额，小时数按同一套口径从"约定还车 → 实际归还"反推，
        // 前台收款时能说清"这钱是超时多久产生的"。
        var overdueHours = order?.ActualReturn is DateTime actual && actual > order.LeaseEnd
            ? PricingResolver.CalcHours(order.LeaseEnd, actual)
            : 0;

        return new SupplementBillDto
        {
            SettleId = s.SettleId,
            OrderId = s.OrderId,
            OrderNo = order?.OrderNo ?? "",
            CustomerName = order?.Customer?.FullName ?? "",
            VehicleNo = order?.Vehicle?.VehicleNo ?? "",
            Amount = s.SupplementAmount,
            PaidAmount = paidAmount,
            Collected = s.SupplementAmount <= 0 || paidAmount >= s.SupplementAmount - 0.01m,
            Channel = paid.OrderByDescending(p => p.PayId).FirstOrDefault()?.PayType,
            PendingPayId = pending?.PayId,
            PendingChannel = pending?.PayType,
            OverdueFee = s.OverdueFee,
            OverdueHours = overdueHours,
            SettleTime = s.SettleTime,
            PaidTime = paid.OrderByDescending(p => p.PayId).FirstOrDefault()?.PayTime
        };
    }

    private async Task<SupplementCollectResultDto> CollectCashAsync(int settleId)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);
            var settle = await _db.SettleRecords.FirstOrDefaultAsync(s => s.SettleId == settleId)
                         ?? throw new Exception("结算记录不存在");
            // 锁订单行：同一张单的现金收款串行化，第二个人拿到锁时已收金额已非 0
            await DbRowLock.LockOrderAsync(_db, settle.OrderId);

            var pays = await _db.PaymentRecords
                .Where(p => p.OrderId == settle.OrderId && p.BizType == "supplement")
                .ToListAsync();
            var paid = pays.Where(p => p.PayStatus == "已支付").Sum(p => p.PayAmount);
            var remaining = Math.Round(settle.SupplementAmount - paid, 2);
            if (remaining <= 0.01m)
            {
                await tx.CommitAsync();
                return new SupplementCollectResultDto
                {
                    SettleId = settleId, Channel = "现金", Amount = 0m, Paid = true,
                    Message = "该结算单的补缴款已收清，无需重复收款"
                };
            }

            var now = DateTime.Now;
            var cashPay = PaymentRecord.Create(settle.OrderId, remaining, "现金", "supplement",
                BuildSupplementOutTradeNo($"JS{settleId}"));
            // 现金没有通道流水号，用「CASH-结算单号-时间」留痕，小票与操作日志能对上
            cashPay.MarkPaid("现金", now, $"CASH-{settleId}-{now:yyyyMMddHHmmss}");
            _db.PaymentRecords.Add(cashPay);
            // 之前为线上收款生成过的待支付补缴单必须作废：钱已经用现金收了，
            // 那张单再挂在顾客端「待补缴」里，顾客可能重复付一次。
            foreach (var stale in pays.Where(p => p.PayStatus == "待支付"))
                stale.Cancel();
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            var orderNo = await _db.RentalOrders.Where(o => o.OrderId == settle.OrderId)
                .Select(o => o.OrderNo).FirstOrDefaultAsync() ?? "";
            var customerId = await _db.RentalOrders.Where(o => o.OrderId == settle.OrderId)
                .Select(o => o.CustomerId).FirstOrDefaultAsync();
            if (customerId > 0)
                await _messages.SendAsync("customer", customerId, "supplement_paid", orderNo, "补缴成功通知",
                    $"订单 {orderNo} 的补缴 ¥{remaining:0.00} 已在门店以现金收讫，感谢您的配合。");

            return new SupplementCollectResultDto
            {
                SettleId = settleId, Channel = "现金", Amount = remaining, Paid = true,
                Message = $"已确认收取现金 ¥{remaining:0.00}，该结算单补缴款已收清"
            };
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private async Task<SupplementCollectResultDto> CollectOnlineAsync(int settleId, string channel)
    {
        var settle = await _db.SettleRecords.FirstOrDefaultAsync(s => s.SettleId == settleId)
                     ?? throw new Exception("结算记录不存在");
        var order = await _db.RentalOrders.FirstOrDefaultAsync(o => o.OrderId == settle.OrderId)
                    ?? throw new Exception("结算记录未关联租赁订单");

        var pays = await _db.PaymentRecords
            .Where(p => p.OrderId == settle.OrderId && p.BizType == "supplement")
            .OrderBy(p => p.PayId)
            .ToListAsync();
        var paidSum = pays.Where(p => p.PayStatus == "已支付").Sum(p => p.PayAmount);
        var remaining = Math.Round(settle.SupplementAmount - paidSum, 2);
        if (remaining <= 0.01m)
            return new SupplementCollectResultDto
            {
                SettleId = settleId, Channel = channel, Amount = 0m, Paid = true,
                Message = "该结算单的补缴款已收清，无需重复收款"
            };

        // 复用已有的待支付单（结算时按线上渠道预生成的那张），避免同一笔钱挂出多个待支付单；
        // 顾客端「待补缴」也只会看到这一张。
        var pending = pays.FirstOrDefault(p => p.PayStatus == "待支付");
        var firstTime = pending == null;
        if (pending == null)
        {
            pending = PaymentRecord.Create(settle.OrderId, remaining, channel, "supplement",
                BuildSupplementOutTradeNo(order.OrderNo));
            _db.PaymentRecords.Add(pending);
        }
        else if (pending.PayType != channel || pending.PayAmount != remaining)
        {
            pending.PreparePayment(channel, remaining);
        }
        await _db.SaveChangesAsync();

        // ── 以下为网关调用：数据库已落库，失败只会导致"有单但拿不到付款参数"，
        //    前台可以再点一次重试，不会留下半条脏数据 ──
        string payUrl = "", qrCode = "", payMode = "";
        bool realPay;
        var subject = $"电动车租赁补缴 {order.OrderNo}";
        // 与订单/会员统一：付款完成后落到同一个「支付结果页」，不再各自回补缴页/订单页，
        // 结果页按 out_trade_no 判定 biz_type 后切文案（补缴单靠 SP- 前缀识别）。
        var returnUrl = await _payConfig.BuildReturnUrlAsync(ClientOrigin, "pay-result");
        var notifyUrl = await _payConfig.BuildNotifyUrlAsync(ClientOrigin, "api/payment/alipay/notify");
        switch (channel)
        {
            case "支付宝":
                payUrl = await _alipay.CreatePagePayAsync(
                    pending.OutTradeNo!, subject, pending.PayAmount, returnUrl, notifyUrl);
                var cfg = await _payConfig.ResolveAsync();
                realPay = cfg is { IsReal: true, CredentialsComplete: true };
                payMode = "page";
                break;
            case "微信":
            {
                var wx = await _wechat.CreatePayAsync(pending.OutTradeNo!, subject, pending.PayAmount, returnUrl);
                payUrl = wx.PayUrl;
                qrCode = wx.QrCode;
                payMode = wx.PayMode;
                realPay = wx.Real;
                break;
            }
            case "银行卡":
                (payUrl, realPay) = await _bank.CreatePayAsync(pending.OutTradeNo!, subject, pending.PayAmount);
                payMode = "page";
                break;
            default:
                throw new Exception($"不支持的收款方式「{channel}」");
        }

        // 首次生成待支付单时同时通知顾客：他可以在顾客端「待补缴」里自己付，
        // 不必必须等前台把二维码递到眼前。
        if (firstTime)
        {
            await _messages.SendAsync(
                "customer", order.CustomerId, "supplement", order.OrderNo, "逾期补缴通知",
                $"订单 {order.OrderNo} 已结算，您尚有余款 ¥{remaining:0.00} 待补缴（逾期/损坏费用）。" +
                $"可用{channel}在线支付，或到店缴纳；逾期可能影响后续租车。");
        }

        return new SupplementCollectResultDto
        {
            SettleId = settleId,
            PayId = pending.PayId,
            Channel = channel,
            Amount = pending.PayAmount,
            Paid = false,
            PayUrl = payUrl,
            QrCode = qrCode,
            PayMode = payMode,
            RealPay = realPay,
            Message = realPay
                ? $"已发起{channel}收款 ¥{pending.PayAmount:0.00}，请引导顾客扫码/打开收银台"
                : $"已生成模拟{channel}收银台 ¥{pending.PayAmount:0.00}（未配置真实凭证，不会真实扣款）"
        };
    }

    /// <summary>收款渠道归一：接受「线下/现金/cash」「微信/微信支付/wechat」等写法， 统一成四个前端展示用的字面量</summary>
    private static string NormalizeChannel(string? raw)
    {
        var s = (raw ?? "").Trim();
        return s switch
        {
            "现金" or "线下" or "cash" or "CASH" => "现金",
            "支付宝" or "alipay" or "Alipay" => "支付宝",
            "微信" or "微信支付" or "wechat" or "Wechat" or "WECHAT" => "微信",
            "银行卡" or "银联" or "bank" or "Bank" => "银行卡",
            "线上" => "线上",       // 旧口径：渠道待顾客在支付页自选
            _ => "现金"
        };
    }

    /// <summary>生成全局唯一的补缴交易号：SP + 订单号 + 时间戳 + 随机后缀</summary>
    private static string BuildSupplementOutTradeNo(string orderNo)
    {
        var ts = DateTime.Now.ToString("yyyyMMddHHmmssfff");
        var rnd = new Random().Next(1000, 9999);
        return $"SP-{orderNo}-{ts}-{rnd}";
    }

    /// <summary>浏览器此刻的地址栏源（X-Client-Origin，缺失时退回 Referer），用于生成支付回跳地址</summary>
    private string? ClientOrigin => PayConfigService.ResolveClientOrigin(
        _http.HttpContext?.Request.Headers["X-Client-Origin"].ToString(),
        _http.HttpContext?.Request.Headers.Referer.ToString());

    /// <summary>支付完成后统一回跳到「支付结果页」（与订单/会员同页），状态由 out_trade_no 反查补缴单</summary>
    private Task<string> BuildReturnUrl() => _payConfig.BuildReturnUrlAsync(ClientOrigin, "pay-result");
}

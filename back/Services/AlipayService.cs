using System.Data;
using System.Text;
using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using ERental.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERental.Services;

/// <summary>
/// 支付宝收单服务：下单（生成收银台跳转链接）、异步回调验签与入账、订单状态查询。
///
/// 设计原则：
///  1. **代码不区分沙箱/生产/模拟**——当前用哪一级由 <see cref="PayConfigService.ResolveAsync"/>
///     按「凭证齐全度」自动判定，本服务只拿判定结果（<see cref="PayRuntime"/>）执行。
///     换环境 = 管理员端改凭证，不碰任何一行业务代码。
///  2. **入账只认验签过的支付数据**——前端跳转回来只代表「用户离开收银台」，不代表付款成功。
///     入账有两路，共用同一套幂等 + 金额校验逻辑 <see cref="ApplyTradeResultAsync"/>：
///     ① 支付宝服务器异步通知（强制验签），生产环境的主路；
///     ② 前端轮询触发的主动查单（alipay.trade.query）兜底入账——沙箱/自部署环境
///     notify_url 打不进 localhost，异步通知天然不通，查单是唯一同步手段。
///  3. **幂等**——同一笔支付可能收到多次回调，已处理过直接返回成功，不重复加钱、不重复改状态。
///  4. **三级兜底**——生产凭证不全 → 沙箱；沙箱也不全 → 内置模拟收银台。
///     保证无网/无资质环境也能把「下单→支付→回调→入账」整条链路演示通。
/// </summary>
public class AlipayService(
    AppDbContext db, PayConfigService payConfig, IOptions<AlipayOptions> opt,
    MemberService member, MessageService messages, ILogger<AlipayService> logger, IHttpClientFactory httpFactory)
{
    private readonly AppDbContext _db = db;
    private readonly PayConfigService _payConfig = payConfig;
    private readonly MemberService _member = member;
    private readonly MessageService _messages = messages;
    private readonly ILogger<AlipayService> _logger = logger;
    private readonly IHttpClientFactory _httpFactory = httpFactory;

    /// <summary>兼容旧调用方的配置视图（NowPay 时代遗留），仅用于读取 SignType 等静态项。</summary>
    private AlipayOptions Options => opt.Value;

    /// <summary>
    /// 创建支付：为指定订单生成支付宝「电脑网站支付」跳转链接。
    /// 返回收银台 URL，前端直接 window.location 跳过去。
    /// </summary>
    /// <param name="orderNo">我方订单号（作为支付宝 out_trade_no）</param>
    /// <param name="subject">商品标题，出现在支付宝收银台</param>
    /// <param name="amount">应付金额（元）</param>
    public async Task<string> CreatePagePayAsync(string orderNo, string subject, decimal amount)
    {
        var rt = await _payConfig.ResolveAsync();

        // 凭证不全（或管理员未启用生产）→ 走内置模拟收银台，保证链路可演示
        if (!rt.IsReal || !rt.CredentialsComplete)
        {
            _logger.LogWarning("当前支付模式为「{Mode}」，订单 {OrderNo} 使用内置模拟收银台", rt.Mode, orderNo);
            var mockUrl = $"/mock-pay?orderNo={Uri.EscapeDataString(orderNo)}&amount={amount:0.00}&subject={Uri.EscapeDataString(subject)}";
            return mockUrl;
        }

        var bizContent = new Dictionary<string, string?>
        {
            ["out_trade_no"] = orderNo,
            ["total_amount"] = amount.ToString("0.00"),
            ["subject"] = subject,
            ["product_code"] = "FAST_INSTANT_TRADE_PAY"
        };

        // 公共参数 + 业务参数。biz_content 必须是 JSON 字符串参与签名
        var parameters = new Dictionary<string, string?>
        {
            ["app_id"] = rt.AppId,
            ["method"] = "alipay.trade.page.pay",
            ["format"] = "JSON",
            ["charset"] = "utf-8",
            ["sign_type"] = Options.SignType,
            ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["version"] = "1.0",
            ["notify_url"] = rt.NotifyUrl,
            ["return_url"] = rt.ReturnUrl,
            ["biz_content"] = ToJson(bizContent)
        };

        var signContent = AlipaySigner.BuildSignContent(parameters);
        var sign = AlipaySigner.Sign(signContent, rt.PrivateKey);
        parameters["sign"] = sign;

        var url = rt.Gateway.TrimEnd('?') + "?" + BuildQueryString(parameters);
        _logger.LogInformation("已为订单 {OrderNo} 生成支付宝收银台链接（模式 {Mode}，金额 {Amount}）",
            orderNo, rt.Mode, amount);
        return url;
    }

    /// <summary>
    /// 处理**真实**支付宝异步通知。
    /// 返回 "success"（纯文本，支付宝收到后停止重推）或 "failure"。
    ///
    /// ⚠️ 本方法只服务真实支付宝回调，必须验签成功才入账。
    /// 内置模拟收银台（/mock-pay）的通知请走 <see cref="HandleMockNotifyAsync"/>：
    /// 模拟通知天生没有签名，若让它走这里必然被验签拒绝（这正是「微信/银行卡支付提示
    /// 通知未受理」的根因——沙箱凭证一旦配好，IsReal=true，验签就强制生效）。
    /// </summary>
    /// <param name="form">回调表单参数（支付宝以 application/x-www-form-urlencoded POST 过来）</param>
    public async Task<string> HandleNotifyAsync(IDictionary<string, string> form)
    {
        var rt = await _payConfig.ResolveAsync();

        // ① 验签：不通过一律拒绝。这一步挡住伪造回调刷单
        var sign = form.TryGetValue("sign", out var s) ? s : "";
        // 回调验签口径与请求签名不同：sign 与 sign_type 都不参与待签串
        var signContent = AlipaySigner.BuildSignContentForVerify(
            form.ToDictionary(kv => kv.Key, kv => (string?)kv.Value));

        // 没有公钥就无法验签。模拟模式下绝不能"没钥匙也放行"——
        // 那等于任何人 POST 一次就能把订单改成已支付。模拟请走 /payment/mock/notify。
        if (string.IsNullOrWhiteSpace(rt.AlipayPublicKey))
        {
            _logger.LogWarning("当前模式「{Mode}」未配置支付宝公钥，无法验签，已拒绝回调（模拟支付请走 /api/payment/mock/notify）", rt.Mode);
            return "failure";
        }

        // 验签用当前生效模式的支付宝公钥（生产/沙箱各一把，避免切环境时串用）
        if (!AlipaySigner.Verify(signContent, sign, rt.AlipayPublicKey))
        {
            // ⚠️ 绝不把回调表单整体打进日志：里面有 sign（可用于重放）以及可能夹带的敏感字段。
            // 只记录「哪些键丢了 / 订单号」，足以定位问题，又无法从中还原任何凭据。
            form.TryGetValue("out_trade_no", out var badOrderNo);
            _logger.LogWarning("支付宝回调验签失败（模式 {Mode}，订单 {OrderNo}，回调参数键：{Keys}）",
                rt.Mode, badOrderNo ?? "(无)", string.Join(",", form.Keys.OrderBy(k => k, StringComparer.Ordinal)));
            return "failure";
        }

        var (ok, _) = await ApplyTradeResultAsync(form, "支付宝");
        return ok ? "success" : "failure";
    }

    /// <summary>
    /// 处理**内置模拟收银台**（/mock-pay）的支付通知。
    ///
    /// 为什么要单独开一个方法，而不是复用了好几年那个 /alipay/notify？
    /// ------------------------------------------------------------------
    /// 模拟收银台是课设环境里替代真实网关的演示件，它发出的通知**没有、也不可能有签名**。
    /// 原先它把通知打到 /alipay/notify，而那个接口一旦检测到"已配支付宝凭证"就强制验签：
    ///   · 没配凭证（模拟模式）→ 不验签 → 微信/银行卡能付通；
    ///   · 配了沙箱凭证（沙箱模式）→ 强制验签 → 模拟通知无 sign → 必被拒 →
    ///     前端只看到一句「支付通知未成功受理，请重试」，而支付宝通道因为走的是
    ///     真实网关、根本不经过模拟收银台，所以一切正常。
    /// 于是出现"只有微信/银行卡失败"的诡异现象——问题不在微信/银行卡，
    /// 而在于**模拟通知被送进了真实回调的验签通道**。
    ///
    /// 拆开之后：真实回调永远验签，模拟通知按"当前是不是生产模式"判定，
    /// 两者共用同一套入账逻辑（<see cref="ApplyTradeResultAsync"/>），口径不会走偏。
    /// </summary>
    public async Task<MockNotifyResult> HandleMockNotifyAsync(IDictionary<string, string> form)
    {
        var rt = await _payConfig.ResolveAsync();

        // ① 生产模式一律拒绝：模拟通知既无签名也无真实资金流，
        //    生产放行等于任何人都能把订单改成「已支付」。
        if (rt.Mode == PayConfigService.ModeProd)
        {
            _logger.LogWarning("生产模式下收到模拟支付通知，已拒绝（订单 {OrderNo}）",
                form.TryGetValue("out_trade_no", out var o) ? o : "(无)");
            return MockNotifyResult.Fail(
                "当前为生产模式，不接受模拟支付通知。请改用真实通道支付，或在「支付配置 → 生产环境」关闭生产开关后重试。");
        }

        // ② 显式标记：这个端点只在演示环境有意义，要求带 MOCK 标记，
        //    避免被无关流量/扫描器误触达。
        form.TryGetValue("trade_type", out var tradeType);
        if (!string.Equals(tradeType, "MOCK", StringComparison.OrdinalIgnoreCase))
            return MockNotifyResult.Fail("模拟通知缺少 trade_type=MOCK 标记，已拒绝。");

        // ③ 支付方式白名单：模拟通道只覆盖系统已有的三条，防止写入脏数据
        var payType = form.TryGetValue("_mock_pay_type", out var t) && !string.IsNullOrWhiteSpace(t) ? t.Trim() : "支付宝";
        if (payType is not ("支付宝" or "微信" or "微信支付" or "银行卡"))
            return MockNotifyResult.Fail($"不支持的模拟支付方式「{payType}」。");

        var (ok, msg) = await ApplyTradeResultAsync(form, payType);
        return ok ? MockNotifyResult.Ok(msg) : MockNotifyResult.Fail(msg);
    }

    /// <summary>
    /// 通知入账的**唯一实现**：支付宝回调、模拟通知、微信回调、银行卡回调共用。
    /// 共用是为了让所有通道的状态机、幂等、金额校验永远一致——
    /// 若各写一份，改了其中一处就会出现"模拟能付通、真实付不通"这类只在上线后才暴露的问题。
    /// </summary>
    /// <returns>(是否受理成功, 说明文案)。文案会直接给到前端，故必须是可以给人看的话。</returns>
    public async Task<(bool Ok, string Message)> ApplyTradeResultAsync(
        IDictionary<string, string> form, string payType)
    {
        form.TryGetValue("out_trade_no", out var outTradeNo);
        form.TryGetValue("trade_status", out var tradeStatus);
        form.TryGetValue("trade_no", out var tradeNo);
        form.TryGetValue("total_amount", out var totalAmount);
        outTradeNo ??= ""; tradeStatus ??= ""; tradeNo ??= ""; totalAmount ??= "";

        if (string.IsNullOrWhiteSpace(outTradeNo))
        {
            _logger.LogWarning("支付通知缺少 out_trade_no");
            return (false, "支付通知缺少订单号（out_trade_no）。");
        }

        // ① 只有 TRADE_SUCCESS / TRADE_FINISHED 才算付款成功
        if (tradeStatus is not ("TRADE_SUCCESS" or "TRADE_FINISHED"))
        {
            _logger.LogInformation("订单 {OrderNo} 收到非成功状态通知：{Status}", outTradeNo, tradeStatus);
            return (true, "该通知为「" + tradeStatus + "」，非付款成功状态，已忽略。");
        }

        // 会员购买（HY 开头交易号）没有租赁订单，直接进会员入账分支——
        // 与订单入账共用本方法，幂等/金额校验/过期保护由 MemberService.ApplyPaidPurchaseAsync 保证
        if (outTradeNo.StartsWith("HY"))
        {
            var memberPaid = decimal.TryParse(totalAmount, out var pa) ? pa : 0m;
            var (mOk, mMsg, _, _) = await _member.ApplyPaidPurchaseAsync(outTradeNo, tradeNo, payType, memberPaid);
            _logger.LogInformation("会员购买 {TradeNo} 入账：{Result}（{Message}）", outTradeNo, mOk ? "成功" : "拒绝", mMsg);
            return (mOk, mMsg);
        }

        // 线上补缴单（SP- 开头交易号）：同一租赁订单上的第二条支付记录（biz_type='supplement'），
        // 金额是逾期/损坏补缴款，与首付款相互独立。入账只改这条支付记录、不动订单状态。
        if (outTradeNo.StartsWith("SP-"))
        {
            return await ApplySupplementPaidAsync(outTradeNo, tradeNo, totalAmount, payType);
        }

        // 租赁订单入账：整段放进「订单行锁 + 事务」里。
        // 触发源不止一个——支付宝会重推通知，前端轮询查单也可能与回调同时到达
        //（一个走 notify、一个走 trade.query，两者是不同线程）。
        // 原实现两边都读到「待支付」，于是各写一次支付记录、各改一次订单状态；
        // 加锁后第二个请求在锁内重读，直接命中幂等分支，不会重复入账。
        return await DbConcurrency.RunAsync(
            () => ApplyOrderPaidAsync(outTradeNo, tradeNo, totalAmount, payType),
            _logger, "支付通知入账", reset: () => _db.ChangeTracker.Clear());
    }

    /// <summary>
    /// 租赁订单入账本体（必须在订单行锁的保护下执行）。
    /// 状态机与金额校验的判定顺序保持原样：幂等 → 已取消 → 状态合法性 → 金额核对 → 写入。
    /// 顺序不能变：先判金额再判幂等，会让重复通知因为金额字段缺失而被判失败、进而被支付宝无限重推。
    /// </summary>
    private async Task<(bool Ok, string Message)> ApplyOrderPaidAsync(
        string outTradeNo, string tradeNo, string totalAmount, string payType)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);

            // 锁内重读订单：从这一行往下，状态判定与写入之间不会再有别人插队
            var order = await DbRowLock.LockOrderByNoAsync(_db, outTradeNo);
            if (order == null)
            {
                _logger.LogWarning("支付通知的订单号 {OrderNo} 在系统中不存在", outTradeNo);
                await tx.RollbackAsync();
                // 让它重推，给「订单先入库、通知先到」的极端时序留余量
                return (false, "订单 " + outTradeNo + " 不存在，请确认订单号后重试。");
            }

            // ② 幂等：已支付过的订单直接确认，不重复入账
            if (order.OrderStatus is "已支付" or "租赁中")
            {
                _logger.LogInformation("订单 {OrderNo} 已是 {Status}，重复通知忽略", outTradeNo, order.OrderStatus);
                await tx.CommitAsync();
                return (true, "订单已是「" + order.OrderStatus + "」，无需重复支付。");
            }
            if (order.OrderStatus == "已取消")
            {
                _logger.LogWarning("订单 {OrderNo} 已取消，仍收到支付通知，需人工退款。交易号 {TradeNo}",
                    outTradeNo, tradeNo);
                await tx.CommitAsync();
                return (true, "订单已取消，该笔收款需人工退款处理。");
            }
            if (order.OrderStatus != "待支付")
            {
                _logger.LogWarning("订单 {OrderNo} 当前状态「{Status}」，不接受支付通知", outTradeNo, order.OrderStatus);
                await tx.CommitAsync();
                return (false, "订单当前状态为「" + order.OrderStatus + "」，不可支付。");
            }

            // ③ 金额核对：防止「改价」类伪造
            var expect = order.TotalRent + order.Deposit;
            if (decimal.TryParse(totalAmount, out var paid) && Math.Abs(paid - expect) > 0.01m)
            {
                _logger.LogWarning("订单 {OrderNo} 金额不符：通知 {Paid}，应付款 {Expect}",
                    outTradeNo, paid, expect);
                await tx.RollbackAsync();
                return (false, $"支付金额不符：通知 ¥{paid:0.00}，订单应付 ¥{expect:0.00}。");
            }

            // ④ 入账：把订单置为已支付，写支付记录
            order.OrderStatus = "已支付";

            var record = await _db.PaymentRecords
                .Where(p => p.OrderId == order.OrderId && p.PayStatus != "已支付")
                .OrderByDescending(p => p.PayId).FirstOrDefaultAsync();

            if (record == null)
            {
                _db.PaymentRecords.Add(new PaymentRecord
                {
                    OrderId = order.OrderId,
                    PayAmount = order.TotalRent + order.Deposit,
                    PayType = payType,
                    PayStatus = "已支付",
                    PayTime = DateTime.Now,
                    TransactionNo = tradeNo
                });
            }
            else
            {
                record.PayStatus = "已支付";
                record.PayType = payType;
                record.PayTime = DateTime.Now;
                record.TransactionNo = tradeNo;
                record.PayAmount = expect;
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            _logger.LogInformation("订单 {OrderNo} 支付成功入账（{PayType}），交易号 {TradeNo}", outTradeNo, payType, tradeNo);
            return (true, $"支付成功，订单 {outTradeNo} 已置为已支付。");
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            // 入账失败必须留下可定位的堆栈：回调端点只会返回「服务端处理异常」，
            // 没有这条日志时，线上只能看到支付宝一直重推而查不出原因。
            // 只记订单号与技术异常，不记 form 里的签名等敏感字段。
            _logger.LogError(ex, "订单 {OrderNo} 支付入账失败", outTradeNo);
            throw;
        }
    }

    /// <summary>
    /// 线上补缴单入账：与首付款入账同一套幂等 + 行锁 + 金额校验，但只改这条补缴支付记录、不动订单状态。
    /// 订单在结算时已是「已完结」，补缴款是后续的独立应收。
    /// </summary>
    private async Task<(bool Ok, string Message)> ApplySupplementPaidAsync(
        string outTradeNo, string tradeNo, string totalAmount, string payType)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);

            // 先按 out_trade_no 找到待支付的补缴记录（无锁读），拿到 payId 后再锁这一行，
            // 让并发回调在锁内重读，避免重复入账。
            var pending = await _db.PaymentRecords
                .FirstOrDefaultAsync(p => p.OutTradeNo == outTradeNo
                                         && p.BizType == "supplement"
                                         && p.PayStatus == "待支付");
            if (pending == null)
            {
                await tx.CommitAsync();
                return (true, "补缴单不存在或已支付，忽略重复通知。");
            }

            var p = await DbRowLock.LockPaymentAsync(_db, pending.PayId);
            if (p == null || p.PayStatus != "待支付")
            {
                await tx.CommitAsync();
                return (true, "补缴单已处理，忽略重复通知。");
            }

            // 金额核对（防止伪造）：实付与补缴金额不符直接拒收
            if (decimal.TryParse(totalAmount, out var paid) && Math.Abs(paid - p.PayAmount) > 0.01m)
            {
                _logger.LogWarning("补缴单 {OutTradeNo} 金额不符：通知 {Paid}，应收 {Expect}",
                    outTradeNo, paid, p.PayAmount);
                await tx.RollbackAsync();
                return (false, $"支付金额不符：通知 ¥{paid:0.00}，补缴应付 ¥{p.PayAmount:0.00}。");
            }

            p.PayStatus = "已支付";
            p.PayType = payType;
            p.PayTime = DateTime.Now;
            p.TransactionNo = tradeNo;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            // 入账成功后通知顾客补缴成功（与退款/补缴通知共用顾客消息通道）
            var orderNo = await _db.RentalOrders.Where(o => o.OrderId == p.OrderId)
                .Select(o => o.OrderNo).FirstOrDefaultAsync() ?? "";
            var cust = await _db.RentalOrders.Where(o => o.OrderId == p.OrderId)
                .Select(o => o.CustomerId).FirstOrDefaultAsync();
            if (cust > 0)
                await _messages.SendAsync("customer", cust, "supplement_paid", orderNo, "补缴成功通知",
                    $"订单 {orderNo} 的逾期补缴 ¥{p.PayAmount:0.00} 已成功支付，感谢您的配合。");

            _logger.LogInformation("补缴单 {OutTradeNo} 支付成功入账（{PayType}），金额 ¥{Amount}",
                outTradeNo, payType, p.PayAmount);
            return (true, $"补缴成功，订单 {orderNo} 补缴 ¥{p.PayAmount:0.00} 已收讫。");
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "补缴单 {OutTradeNo} 入账异常", outTradeNo);
            return (false, "补缴入账处理异常，请稍后重试。");
        }
    }

    /// <summary>
    /// 支付宝接线自检：不依赖订单，直接向当前模式对应的网关发一次最小请求，判断配置是否可用。
    ///
    /// 为什么需要它：接入支付宝最常见的故障是「后台登记的应用公钥与本地私钥不配对」，
    /// 表现为所有下单/查询都报 isv.invalid-signature。若靠"下单→失败"来试，
    /// 既慢又难定位。这里用一个必然不存在的订单号发查询：
    ///   - 返回「交易不存在」类错误 → **签名已被网关接受**，配置正确；
    ///   - 返回 isv.invalid-signature → 密钥不配对，同时给出本地私钥对应的应用公钥，
    ///     管理员拿去开放平台「接口加签方式」重新上传即可。
    /// 教学价值：把"配置是否正确"变成一个可一键回答的问题。
    /// </summary>
    public async Task<AlipaySelfCheckDto> SelfCheckAsync()
    {
        var rt = await _payConfig.ResolveAsync();
        var dto = new AlipaySelfCheckDto
        {
            Mode = rt.Mode,
            Enabled = rt.IsReal,
            Configured = rt.CredentialsComplete,
            AppId = rt.AppId,
            Gateway = rt.Gateway
        };

        if (rt.Mode == PayConfigService.ModeMock)
        {
            dto.Message = "当前为「模拟」支付模式：未配置可用的沙箱/生产凭证，走内置模拟收银台（链路完整，无需联网）。" +
                          "如需切换真实支付，请在下方填写沙箱或生产凭证。";
            return dto;
        }

        if (string.IsNullOrWhiteSpace(rt.PrivateKey))
        {
            dto.Message = "未配置应用私钥";
            return dto;
        }

        // 无论公私钥是否配对，先把本地私钥对应的「应用公钥」算出来，方便管理员核对
        try
        {
            dto.ExpectedAppPublicKey = AlipaySigner.DeriveApplicationPublicKey(rt.PrivateKey);
            dto.ExpectedAppPublicKeyFingerprint = AlipaySigner.PublicKeyFingerprint(dto.ExpectedAppPublicKey);
            dto.PrivateKeyFormat = AlipaySigner.DetectPrivateKeyFormat(rt.PrivateKey);
        }
        catch (Exception ex)
        {
            // 只回异常类型名，不回 Message —— 私钥解析失败的原始消息可能包含密钥片段
            dto.AppPublicKeyError = $"私钥解析失败（{ex.GetType().Name}），请检查是否为完整的 PKCS#8 / PKCS#1 私钥";
            _logger.LogWarning("自检：由私钥推导应用公钥失败（{ExType}）", ex.GetType().Name);
        }

        try
        {
            var parameters = new Dictionary<string, string?>
            {
                ["app_id"] = rt.AppId,
                ["method"] = "alipay.trade.query",
                ["format"] = "JSON",
                ["charset"] = "utf-8",
                ["sign_type"] = Options.SignType,
                ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["version"] = "1.0",
                // 用一个必然不存在的订单号：只要签名通过，网关会回「交易不存在」，
                // 这恰恰证明「签名被接受」，是我们想要的成功信号。
                ["biz_content"] = ToJson(new Dictionary<string, string?> { ["out_trade_no"] = "__SELFCHECK__" })
            };
            parameters["sign"] = AlipaySigner.Sign(AlipaySigner.BuildSignContent(parameters), rt.PrivateKey);

            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var resp = await http.PostAsync(rt.Gateway.TrimEnd('?') + "?charset=utf-8",
                new FormUrlEncodedContent(parameters
                    .Where(kv => kv.Value != null)
                    .ToDictionary(kv => kv.Key, kv => kv.Value!)));
            var body = await ReadBodyAsync(resp);
            // 先脱敏（清掉私钥/公钥字面值）再截断：网关回显里可能带 sign 与证书片段，
            // 这道处理保证「查看原始响应」这个排查入口不会成为密钥外泄通道。
            dto.RawResponse = SecretMasker.Truncate(
                SecretMasker.Scrub(body, rt.PrivateKey, rt.AlipayPublicKey), 800);

            if (body.Contains("isv.invalid-signature"))
            {
                dto.SignatureAccepted = false;
                dto.Message = "签名被沙箱拒绝：沙箱后台登记的「应用公钥」与你本地私钥不配对。" +
                              $"请核对——本地私钥（{dto.PrivateKeyFormat}）对应的应用公钥指纹为 " +
                              $"{dto.ExpectedAppPublicKeyFingerprint}，把它整段重新上传到沙箱「接口加签方式→自定义密钥→应用公钥」。";
            }
            else if (body.Contains("isv.missing-signature"))
            {
                dto.SignatureAccepted = false;
                dto.Message = "沙箱未收到签名，检查请求构造";
            }
            else if (body.Contains("TRADE_NOT_EXIST") || body.Contains("40004")
                     || body.Contains("\"code\":\"40002\""))
            {
                // 40002 也可能含 ACQ.TRADE_NOT_EXIST 之类，但既然没报 invalid-signature，
                // 就说明签名这一关已经过了。
                dto.SignatureAccepted = true;
                dto.Message = "签名已被沙箱接受，配置正确（返回的业务错误属正常，因为订单号是探测用的假号）";
            }
            else
            {
                dto.SignatureAccepted = true;
                dto.Message = "沙箱已响应且未报签名错误，配置可用";
            }
            return dto;
        }
        catch (Exception ex)
        {
            // 只回异常类型与脱敏后的短消息：HttpRequestException 的 Message 可能带完整 URL
            // （URL 里含 app_id 等参数），TaskCanceledException 则是超时。
            _logger.LogWarning("自检请求异常：{ExType} {Message}", ex.GetType().Name,
                SecretMasker.Scrub(ex.Message, rt.PrivateKey, rt.AlipayPublicKey));
            dto.Message = ex is TaskCanceledException or OperationCanceledException
                ? "自检请求超时：网关在 15 秒内未响应，请检查网络或网关地址"
                : $"自检请求异常（{ex.GetType().Name}），请检查网络与网关地址是否正确";
            return dto;
        }
    }

    /// <summary>
    /// 主动查询支付宝交易状态（前端轮询 + 回跳兜底）。
    ///
    /// 查到「付款成功」不能只是返回字符串——必须当场走与异步回调**完全相同**的入账逻辑
    /// <see cref="ApplyTradeResultAsync"/>。沙箱/自部署环境里支付宝服务器往往打不进本机
    /// notify_url（localhost 不可达），异步通知这条路天然不通，主动查单是唯一同步手段；
    /// 若只查不入账，前端轮询会永远拿到「待支付」（沙箱已付款、订单却没支付即此因）。
    /// 入账逻辑本身幂等且带金额校验，查单与回调双路触发也不会重复加钱。
    /// </summary>
    /// <returns>无返回值：查单的副作用是入账，状态由调用方重新读库获取。</returns>
    public async Task QueryTradeStatusAsync(string orderNo)
    {
        var order = await _db.RentalOrders.FirstOrDefaultAsync(o => o.OrderNo == orderNo);
        if (order == null) return;

        var rt = await _payConfig.ResolveAsync();
        if (!rt.IsReal || !rt.CredentialsComplete)
            return;   // 模拟模式：本地状态即真相

        string code, tradeStatus, tradeNo, totalAmount;
        try
        {
            (code, tradeStatus, tradeNo, totalAmount) = await QueryAlipayAsync(orderNo, rt);
        }
        catch (Exception ex)
        {
            // 只记异常类型，不记 Message —— 查询请求的 Message 里含 app_id、订单号等请求参数
            _logger.LogWarning("查询支付宝订单 {OrderNo} 状态失败（{ExType}），回退本地状态", orderNo, ex.GetType().Name);
            return;   // 查询失败不影响本地状态，兜底
        }

        if (code != "10000")
        {
            if (code == "40004")
                return;
            // 40002 + isv.invalid-signature = 密钥不配对；其余业务错误回退本地状态即可
            _logger.LogWarning("查询订单 {OrderNo} 未成功：code={Code}（40004=交易不存在，40002=验签失败/参数错误）",
                orderNo, code);
            return;
        }

        if (tradeStatus is "TRADE_SUCCESS" or "TRADE_FINISHED")
        {
            // 查单确认已付款 → 与回调共用同一入账逻辑（幂等、金额校验、写支付记录）
            var (ok, msg) = await ApplyTradeResultAsync(new Dictionary<string, string>
            {
                ["out_trade_no"] = orderNo,
                ["trade_status"] = tradeStatus,
                ["trade_no"] = tradeNo,
                ["total_amount"] = totalAmount
            }, "支付宝");
            _logger.LogInformation("订单 {OrderNo} 查单兜底入账：{Result}（{Message}）", orderNo, ok ? "成功" : "拒绝", msg);
        }
    }

    /// <summary>
    /// 会员购买（HY 开头交易号）的状态查询：不经过租赁订单表，直接向网关查单。
    /// 模拟模式返回占位 code="MOCK"——模拟收银台的入账由 mock/notify 主动推送，无需查单。
    /// 网关异常会向上抛出，由调用方决定兜底策略。
    /// </summary>
    public async Task<(string Code, string TradeStatus, string TradeNo, string TotalAmount)> QueryTradeRawAsync(string outTradeNo)
    {
        var rt = await _payConfig.ResolveAsync();
        if (!rt.IsReal || !rt.CredentialsComplete)
            return ("MOCK", "", "", "");
        return await QueryAlipayAsync(outTradeNo, rt);
    }

    /// <summary>
    /// 支付宝**原路退款**（alipay.trade.refund）。
    ///
    /// 为什么必须有这个方法：在此之前 `PaymentService.Refund` 只把 `payment_record.refund_amount`
    /// 改成"已退款"，**没有向支付宝发起任何请求**——账面退了、钱还在商户账户里。
    /// 真实收款一旦发生，这就是资金风险，也与需求 4.2.5「剩余押金原路退还」直接冲突。
    ///
    /// 幂等：支付宝以 <paramref name="outRequestNo"/> 作为退款请求号，**同一个请求号重复提交
    /// 只会退一次**。调用方用「支付记录 ID + 累计应退金额」拼出请求号（见 PaymentService），
    /// 因此"网关成功但本地事务回滚"后重试是安全的，不会退两笔。
    ///
    /// 部分退款：支付宝按「累计退款额」语义处理同一笔交易的多次退款，每次传**本次退款金额**
    /// 与不同的请求号即可；调用方负责保证每次金额不自相矛盾。
    /// </summary>
    /// <returns>Ok=false 时 Message 是可直接展示给前台/财务的中文原因。</returns>
    public async Task<(bool Ok, string Message)> RefundAsync(
        string outTradeNo, string? tradeNo, string outRequestNo, decimal amount, string? reason = null)
    {
        // 退款定位：out_trade_no（商户订单号）与 trade_no（支付宝交易号）二选一，
        // 两者都带时支付宝优先用 trade_no。payment_record.transaction_no 存的是支付宝 trade_no，
        // 首付款/补缴单的商户订单号另由调用方用 outTradeNo 传入（订单号 或 SP- 补缴号），
        // 绝不能再把 trade_no 当成 out_trade_no 去查，否则会报「交易不存在」。
        if (string.IsNullOrWhiteSpace(outTradeNo) && string.IsNullOrWhiteSpace(tradeNo))
            return (false, "缺少支付宝订单号/交易号，无法原路退款；请在支付宝商户后台按订单号手工退款。");
        if (amount <= 0)
            return (false, "退款金额必须大于 0。");

        var rt = await _payConfig.ResolveAsync();
        if (!rt.IsReal || !rt.CredentialsComplete)
            return (false, "当前为模拟支付模式，未发生真实资金划转。");

        var bizContent = new Dictionary<string, string?>
        {
            ["out_trade_no"] = string.IsNullOrWhiteSpace(outTradeNo) ? null : outTradeNo,
            ["trade_no"] = string.IsNullOrWhiteSpace(tradeNo) ? null : tradeNo,
            ["refund_amount"] = amount.ToString("0.00"),
            ["out_request_no"] = outRequestNo,
            ["refund_reason"] = string.IsNullOrWhiteSpace(reason) ? null : SecretMasker.Truncate(reason.Trim(), 200)
        };

        var parameters = new Dictionary<string, string?>
        {
            ["app_id"] = rt.AppId,
            ["method"] = "alipay.trade.refund",
            ["format"] = "JSON",
            ["charset"] = "utf-8",
            ["sign_type"] = Options.SignType,
            ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["version"] = "1.0",
            ["biz_content"] = ToJson(bizContent)
        };
        parameters["sign"] = AlipaySigner.Sign(AlipaySigner.BuildSignContent(parameters), rt.PrivateKey);

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var resp = await http.PostAsync(rt.Gateway.TrimEnd('?') + "?charset=utf-8",
                new FormUrlEncodedContent(parameters
                    .Where(kv => kv.Value != null)
                    .ToDictionary(kv => kv.Key, kv => kv.Value!)));
            resp.EnsureSuccessStatusCode();
            var body = await ReadBodyAsync(resp);

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("alipay_trade_refund_response", out var el))
                return (false, "支付宝退款响应格式异常，请到商户后台核实退款状态后再重试。");

            string Get(string name) =>
                el.ValueKind == System.Text.Json.JsonValueKind.Object && el.TryGetProperty(name, out var v)
                    ? v.GetString() ?? "" : "";

            var code = Get("code");
            var fundChange = Get("fund_change");
            var refundFee = Get("refund_fee");

            if (code == "10000" && (fundChange is "Y" or "" || !string.IsNullOrWhiteSpace(refundFee)))
            {
                _logger.LogInformation("支付宝退款受理成功：交易号 {TradeNo}，本次 ¥{Amount}，请求号 {RequestNo}",
                    outTradeNo, amount, outRequestNo);
                return (true, $"支付宝已受理退款 ¥{amount:0.00}（请求号 {outRequestNo}）。");
            }

            // 失败文案要用支付宝给的 msg/sub_msg（如「交易不存在」「退款金额大于可退金额」），
            // 但先脱敏再截断，避免响应里夹带的 sign / 证书片段进入前端。
            var raw = $"{Get("msg")} {Get("sub_msg")}".Trim();
            var detail = string.IsNullOrWhiteSpace(raw) ? $"code={code}" : SecretMasker.Truncate(raw, 200);
            _logger.LogWarning("支付宝退款未成功：交易号 {TradeNo}，code={Code}，原因 {Detail}",
                outTradeNo, code, detail);
            return (false, $"支付宝退款未成功：{detail}");
        }
        catch (Exception ex)
        {
            // 只回类型名：HttpRequestException 的 Message 可能带完整网关 URL 与请求参数
            _logger.LogWarning("支付宝退款请求异常：交易号 {TradeNo}，{ExType}", outTradeNo, ex.GetType().Name);
            return (false, ex is TaskCanceledException or OperationCanceledException
                ? "支付宝退款请求超时，结果未知；请先在商户后台核实，切勿直接重复提交。"
                : $"支付宝退款请求异常（{ex.GetType().Name}），结果未知；请先核实再重试。");
        }
    }

    /// <summary>向支付宝网关发起 alipay.trade.query 并解析响应（HTTP/解析异常向上抛）</summary>
    private async Task<(string Code, string TradeStatus, string TradeNo, string TotalAmount)> QueryAlipayAsync(
        string outTradeNo, PayRuntime rt)
    {
        var bizContent = ToJson(new Dictionary<string, string?> { ["out_trade_no"] = outTradeNo });
        var parameters = new Dictionary<string, string?>
        {
            ["app_id"] = rt.AppId,
            ["method"] = "alipay.trade.query",
            ["format"] = "JSON",
            ["charset"] = "utf-8",
            ["sign_type"] = Options.SignType,
            ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["version"] = "1.0",
            ["biz_content"] = bizContent
        };
        parameters["sign"] = AlipaySigner.Sign(AlipaySigner.BuildSignContent(parameters), rt.PrivateKey);

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        // charset 必须作为 URL 查询串参数（支付宝网关要求），放 body 会被判 isv.invalid-signature
        var resp = await http.PostAsync(rt.Gateway.TrimEnd('?') + "?charset=utf-8",
            new FormUrlEncodedContent(parameters
                .Where(kv => kv.Value != null)
                .ToDictionary(kv => kv.Key, kv => kv.Value!)));
        resp.EnsureSuccessStatusCode();
        var body = await ReadBodyAsync(resp);

        // 解析响应。实测网关（含沙箱）标准返回是平铺节点：{"alipay_trade_query_response":{...},"sign":"..."}，
        // 与官方文档一致；历史上还见过 {"alipay_trade_query":{"response":"转义JSON"}} 的嵌套形态，
        // code!=10000 时 response 甚至退化为转义字符串——两种形态都兼容，避免解析路径过窄。
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("alipay_trade_query_response", out var respEl))
        {
            respEl = doc.RootElement.GetProperty("alipay_trade_query").GetProperty("response");
            if (respEl.ValueKind == System.Text.Json.JsonValueKind.String)
                respEl = System.Text.Json.JsonDocument.Parse(respEl.GetString() ?? "{}").RootElement.Clone();
        }
        string Get(string name) =>
            respEl.ValueKind == System.Text.Json.JsonValueKind.Object &&
            respEl.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        return (Get("code"), Get("trade_status"), Get("trade_no"), Get("total_amount"));
    }

    /// <summary>构造查询串（值做 URL 编码，签名已完成）</summary>
    private static string BuildQueryString(IDictionary<string, string?> parameters) =>
        string.Join("&", parameters
            .Where(kv => kv.Value != null)
            .Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value!)}"));

    /// <summary>极简 JSON 构造：支付宝 biz_content 只是键值对，无需引入序列化库</summary>
    private static string ToJson(IDictionary<string, string?> dict)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (k, v) in dict)
        {
            if (v == null) continue;
            if (!first) sb.Append(',');
            sb.Append('"').Append(k).Append("\":\"").Append(Escape(v)).Append('"');
            first = false;
        }
        return sb.Append('}').ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// 读取支付宝网关响应体。
    ///
    /// 为什么不能直接用 ReadAsStringAsync：沙箱网关会用响应头里的 charset（常见为 GBK）声明编码，
    /// 而 .NET 默认只认 UTF-8 / ASCII 等少数几项，遇到 GBK 会直接抛「不支持的编码名」异常，
    /// 把一次正常的查询变成 500。这里改为先取字节流，再按响应声明的 charset 解码，
    /// 不支持时依次回退 GBK → UTF-8，保证不再因编码问题崩掉。
    /// </summary>
    private static async Task<string> ReadBodyAsync(HttpResponseMessage resp)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        var charset = resp.Content.Headers.ContentType?.CharSet?.Trim('"', ' ');
        if (!string.IsNullOrEmpty(charset))
        {
            try { return Encoding.GetEncoding(charset).GetString(bytes); }
            catch { /* 编码名不被支持，落到下面的兜底 */ }
        }
        try { return Encoding.GetEncoding("GBK").GetString(bytes); }
        catch { return Encoding.UTF8.GetString(bytes); }
    }
}

using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using ERental.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERental.Services;

///<summary>支付宝收单服务：下单（生成收银台跳转链接）、异步回调验签与入账、订单状态查询</summary>
public class AlipayService(AppDbContext db,PayConfigService payConfig,IOptions<AlipayOptions> opt,MemberService member,MessageService messages,ILogger<AlipayService> logger,IHttpClientFactory httpFactory,IConfiguration configuration)   //NOSONAR 依赖由 DI 容器逐个注入，合并参数会改变公共构造函数签名并影响所有调用方，故保留 8 个
{
    private readonly AppDbContext _db=db;
    private readonly PayConfigService _payConfig=payConfig;
    private readonly MemberService _member=member;
    private readonly MessageService _messages=messages;
    private readonly ILogger<AlipayService> _logger=logger;
    private readonly IHttpClientFactory _httpFactory=httpFactory;
    private readonly IConfiguration _configuration=configuration;

    ///<summary>兼容旧调用方的配置视图（NowPay 时代遗留），仅用于读取 SignType 等静态项</summary>
    private AlipayOptions Options=>opt.Value;

    //支付宝协议参数名与固定值：同一套键在「下单/查单/退款/自检」四处请求与回调解析中复用
    private const string AlipayParamOutTradeNo="out_trade_no";
    private const string AlipayParamTotalAmount="total_amount";
    private const string AlipayParamAppId="app_id";
    private const string AlipayParamMethod="method";
    private const string AlipayParamFormat="format";
    private const string AlipayParamCharset="charset";
    private const string AlipayCharsetUtf8="utf-8";
    private const string AlipayParamSignType="sign_type";
    private const string AlipayParamTimestamp="timestamp";
    private const string AlipayParamVersion="version";
    private const string AlipayParamBizContent="biz_content";
    private const string AlipayParamTradeStatus="trade_status";
    private const string AlipayParamTradeNo="trade_no";
    private const string AlipayFormatJson="JSON";
    private const string AlipayVersionV1="1.0";

    ///<summary>支付宝 timestamp 参数要求的格式（本地时间，与网关约定一致）</summary>
    private const string DateFormatMinute="yyyy-MM-dd HH:mm:ss";

    ///<summary>创建支付：为指定订单生成支付宝「电脑网站支付」跳转链接付款完成后的同步回跳地址</summary>
    public async Task<string> CreatePagePayAsync(string orderNo,string subject,decimal amount,string? returnUrl=null,string? notifyUrl=null)
    {
        var rt=await _payConfig.ResolveAsync();

        //凭证不全（或管理员未启用生产）→ 走内置模拟收银台，保证链路可演示
        if(!rt.IsReal||!rt.CredentialsComplete)
        {
            _logger.LogWarning("当前支付模式为「{Mode}」，订单 {OrderNo} 使用内置模拟收银台",rt.Mode,orderNo);
            var mockUrl=$"/mock-pay?orderNo={Uri.EscapeDataString(orderNo)}&amount={amount:0.00}&subject={Uri.EscapeDataString(subject)}";
            return mockUrl;
        }

        var bizContent=new Dictionary<string,string?>
        {
            [AlipayParamOutTradeNo]=orderNo,
            [AlipayParamTotalAmount]=amount.ToString("0.00"),
            ["subject"]=subject,
            ["product_code"]="FAST_INSTANT_TRADE_PAY"
        };

        //公共参数+业务参数
        var parameters=new Dictionary<string,string?>
        {
            [AlipayParamAppId]=rt.AppId,
            [AlipayParamMethod]="alipay.trade.page.pay",
            [AlipayParamFormat]=AlipayFormatJson,
            [AlipayParamCharset]=AlipayCharsetUtf8,
            [AlipayParamSignType]=Options.SignType,
            [AlipayParamTimestamp]=DateTime.Now.ToString(DateFormatMinute),
            [AlipayParamVersion]=AlipayVersionV1,
            ["notify_url"]=string.IsNullOrWhiteSpace(notifyUrl)?rt.NotifyUrl:notifyUrl.Trim(),
            ["return_url"]=string.IsNullOrWhiteSpace(returnUrl)?rt.ReturnUrl:returnUrl.Trim(),
            [AlipayParamBizContent]=ToJson(bizContent)
        };

        var signContent=AlipaySigner.BuildSignContent(parameters);
        var sign=AlipaySigner.Sign(signContent,rt.PrivateKey);
        parameters["sign"]=sign;

        var url=rt.Gateway.TrimEnd('?')+"?"+BuildQueryString(parameters);
        _logger.LogInformation("已为订单 {OrderNo} 生成支付宝收银台链接（模式 {Mode}，金额 {Amount}）",orderNo,rt.Mode,amount);
        return url;
    }

    ///<summary>处理真实支付宝异步通知</summary>
    public async Task<string> HandleNotifyAsync(IDictionary<string,string> form)
    {
        var rt=await _payConfig.ResolveAsync();

        //① 验签：不通过一律拒绝
        var sign=form.TryGetValue("sign",out var s)?s:"";
        //回调验签口径与请求签名不同：sign 与 sign_type 都不参与待签串
        var signContent=AlipaySigner.BuildSignContentForVerify(
            form.ToDictionary(kv=>kv.Key,kv=>(string?)kv.Value));

        //没有公钥就无法验签
        if(string.IsNullOrWhiteSpace(rt.AlipayPublicKey))
        {
            _logger.LogWarning("当前模式「{Mode}」未配置支付宝公钥，无法验签，已拒绝回调（模拟支付请走 /api/payment/mock/notify）",rt.Mode);
            return "failure";
        }

        //验签用当前生效模式的支付宝公钥
        if(!AlipaySigner.Verify(signContent,sign,rt.AlipayPublicKey))
        {
            //绝不把回调表单整体打进日志：里面有 sign（可用于重放）以及可能夹带的敏感字段
            form.TryGetValue(AlipayParamOutTradeNo,out var badOrderNo);
            _logger.LogWarning("支付宝回调验签失败（模式 {Mode}，订单 {OrderNo}，回调参数键：{Keys}）",rt.Mode,badOrderNo??"(无)",string.Join(",",form.Keys.OrderBy(k=>k,StringComparer.Ordinal)));
            return "failure";
        }

        //② 重放防护：验签通过后，5 分钟窗口内「完全相同的报文」只放行一次
        form.TryGetValue("notify_id",out var notifyId);
        form.TryGetValue(AlipayParamOutTradeNo,out var outTradeNo);
        form.TryGetValue(AlipayParamTradeStatus,out var tradeStatus);
        form.TryGetValue(AlipayParamTradeNo,out var tradeNo);
        form.TryGetValue(AlipayParamTotalAmount,out var totalAmount);
        var replayKey=PayNotifyGuard.BuildKey("alipay",sign,notifyId,outTradeNo,tradeStatus,tradeNo,totalAmount);
        if(PayNotifyGuard.IsDuplicate(replayKey))
        {
            _logger.LogInformation("支付宝回调重复投递已忽略（订单 {OrderNo}）",outTradeNo??"(无)");
            return "success";//幂等确认：告诉支付宝已收到，停止重推
        }

        var(ok,_)=await ApplyTradeResultAsync(form,"支付宝");
        return ok?"success":"failure";
    }

    ///<summary>处理内置模拟收银台（/mock-pay）的支付通知模拟通知表单请求是否来自本机（loopback）</summary>
    public async Task<MockNotifyResult> HandleMockNotifyAsync(IDictionary<string,string> form,bool isLoopback=false)
    {
        var rt=await _payConfig.ResolveAsync();
        form.TryGetValue(AlipayParamOutTradeNo,out var mockOrderNo);

        //① 服务端开关：默认关闭
        var allowRaw=_configuration["Pay:AllowMockNotify"];
        var allowMock=allowRaw is "1" or "true" or "True" or "TRUE";
        if(!allowMock)
        {
            _logger.LogWarning("收到模拟支付通知，但服务端未开启 Pay:AllowMockNotify，已拒绝（订单 {OrderNo}）",mockOrderNo??"(无)");
            return MockNotifyResult.Fail("服务端未开启模拟支付通知（Pay:AllowMockNotify），已拒绝。");
        }

        //② 仅允许本机（loopback）来源
        if(!isLoopback)
        {
            _logger.LogWarning("模拟支付通知来源非本机，已拒绝（订单 {OrderNo}）",mockOrderNo??"(无)");
            return MockNotifyResult.Fail("模拟支付通知仅允许本机访问，已拒绝。");
        }

        //③ 生产模式一律拒绝：模拟通知既无签名也无真实资金流
        if(rt.Mode==PayConfigService.ModeProd)
        {
            _logger.LogWarning("生产模式下收到模拟支付通知，已拒绝（订单 {OrderNo}）",mockOrderNo??"(无)");
            return MockNotifyResult.Fail("当前为生产模式，不接受模拟支付通知。请改用真实通道支付，或在「支付配置 → 生产环境」关闭生产开关后重试。");
        }

        //④ 显式标记
        form.TryGetValue("trade_type",out var tradeType);
        if(!string.Equals(tradeType,"MOCK",StringComparison.OrdinalIgnoreCase)) return MockNotifyResult.Fail("模拟通知缺少 trade_type=MOCK 标记，已拒绝。");

        //⑤ 支付方式白名单
        var payType=form.TryGetValue("_mock_pay_type",out var t)&&!string.IsNullOrWhiteSpace(t)?t.Trim():"支付宝";
        if(payType is not("支付宝" or "微信" or "微信支付" or "银行卡")) return MockNotifyResult.Fail($"不支持的模拟支付方式「{payType}」。");

        var(ok,msg)=await ApplyTradeResultAsync(form,payType);
        return ok?MockNotifyResult.Ok(msg):MockNotifyResult.Fail(msg);
    }

    ///<summary>通知入账的唯一实现：支付宝回调、模拟通知、微信回调、银行卡回调共用</summary>
    public async Task<(bool Ok,string Message)> ApplyTradeResultAsync(IDictionary<string,string> form,string payType)
    {
        form.TryGetValue(AlipayParamOutTradeNo,out var outTradeNo);
        form.TryGetValue(AlipayParamTradeStatus,out var tradeStatus);
        form.TryGetValue(AlipayParamTradeNo,out var tradeNo);
        form.TryGetValue(AlipayParamTotalAmount,out var totalAmount);
        outTradeNo ??= "";tradeStatus ??= "";tradeNo ??= "";totalAmount ??= "";

        if(string.IsNullOrWhiteSpace(outTradeNo))
        {
            _logger.LogWarning("支付通知缺少 out_trade_no");
            return(false,"支付通知缺少订单号（out_trade_no）。");
        }

        //① 只有 TRADE_SUCCESS/TRADE_FINISHED 才算付款成功
        if(tradeStatus is not("TRADE_SUCCESS" or "TRADE_FINISHED"))
        {
            _logger.LogInformation("订单 {OrderNo} 收到非成功状态通知：{Status}",outTradeNo,tradeStatus);
            return(true,"该通知为「"+tradeStatus+"」，非付款成功状态，已忽略。");
        }

        //会员购买（HY 开头交易号）没有租赁订单，直接进会员入账分支
        if(outTradeNo.StartsWith("HY"))
        {
            //与订单/补缴入账同一严格解析口径
            var memberPaid=PayNotifyGuard.TryParseAmount(totalAmount,out var pa)?pa:0m;
            var(mOk,mMsg,_,_)=await _member.ApplyPaidPurchaseAsync(outTradeNo,tradeNo,payType,memberPaid);
            _logger.LogInformation("会员购买 {TradeNo} 入账：{Result}（{Message}）",outTradeNo,mOk?"成功":"拒绝",mMsg);
            return(mOk,mMsg);
        }

        //线上补缴单（SP- 开头交易号）：同一租赁订单上的第二条支付记录（biz_type='supplement'）
        if(outTradeNo.StartsWith("SP-"))
        {
            return await ApplySupplementPaidAsync(outTradeNo,tradeNo,totalAmount,payType);
        }

        //租赁订单入账：整段放进「订单行锁+事务」里
        return await DbConcurrency.RunAsync(()=>ApplyOrderPaidAsync(outTradeNo,tradeNo,totalAmount,payType),_logger,"支付通知入账",reset: ()=>_db.ChangeTracker.Clear());
    }

    ///<summary>租赁订单入账本体（必须在订单行锁的保护下执行）</summary>
    private async Task<(bool Ok,string Message)> ApplyOrderPaidAsync(string outTradeNo,string tradeNo,string totalAmount,string payType)
    {
        await using var tx=await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);

            //锁内重读订单：从这一行往下，状态判定与写入之间不会再有别人插队
            var order=await DbRowLock.LockOrderByNoAsync(_db,outTradeNo);
            if(order==null)
            {
                _logger.LogWarning("支付通知的订单号 {OrderNo} 在系统中不存在",outTradeNo);
                await tx.RollbackAsync();
                //让它重推，给「订单先入库、通知先到」的极端时序留余量
                return(false,"订单 "+outTradeNo+" 不存在，请确认订单号后重试。");
            }

            //② 幂等：已支付过的订单直接确认，不重复入账
            if(order.OrderStatus is "已支付" or "租赁中")
            {
                _logger.LogInformation("订单 {OrderNo} 已是 {Status}，重复通知忽略",outTradeNo,order.OrderStatus);
                await tx.CommitAsync();
                return(true,"订单已是「"+order.OrderStatus+"」，无需重复支付。");
            }
            if(order.OrderStatus=="已取消")
            {
                _logger.LogWarning("订单 {OrderNo} 已取消，仍收到支付通知，需人工退款。交易号 {TradeNo}",outTradeNo,tradeNo);
                await tx.CommitAsync();
                return(true,"订单已取消，该笔收款需人工退款处理。");
            }
            if(order.OrderStatus!="待支付")
            {
                _logger.LogWarning("订单 {OrderNo} 当前状态「{Status}」，不接受支付通知",outTradeNo,order.OrderStatus);
                await tx.CommitAsync();
                return(false,"订单当前状态为「"+order.OrderStatus+"」，不可支付。");
            }

            //③ 金额核对
            var expect=order.TotalRent+order.Deposit;
            if(!PayNotifyGuard.TryParseAmount(totalAmount,out var paid))
            {
                _logger.LogWarning("订单 {OrderNo} 支付通知缺少合法金额（total_amount={Amount}），拒绝入账",outTradeNo,totalAmount);
                await tx.RollbackAsync();
                return(false,"支付通知缺少合法金额（total_amount），拒绝入账。");
            }
            if(Math.Abs(paid-expect) > 0.01m)
            {
                _logger.LogWarning("订单 {OrderNo} 金额不符：通知 {Paid}，应付款 {Expect}",outTradeNo,paid,expect);
                await tx.RollbackAsync();
                return(false,$"支付金额不符：通知 ¥{paid:0.00}，订单应付 ¥{expect:0.00}。");
            }

            //④ 入账：把订单置为已支付，写支付记录
            order.ChangeStatus("已支付");

            var record=await _db.PaymentRecords.Where(p=>p.OrderId==order.OrderId&&p.PayStatus!="已支付").OrderByDescending(p=>p.PayId).FirstOrDefaultAsync();

            if(record==null)
            {
                var fresh=PaymentRecord.Create(order.OrderId,order.TotalRent+order.Deposit,payType,"order",null,"已支付");
                fresh.MarkPaid(payType,DateTime.Now,tradeNo);
                _db.PaymentRecords.Add(fresh);
            }
            else
            {
                record.MarkPaid(payType,DateTime.Now,tradeNo,expect);
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            _logger.LogInformation("订单 {OrderNo} 支付成功入账（{PayType}），交易号 {TradeNo}",outTradeNo,payType,tradeNo);
            return(true,$"支付成功，订单 {outTradeNo} 已置为已支付。");
        }
        catch(Exception ex)   //NOSONAR 已记完整堆栈并原样重抛：上层要按异常类型（DbUpdateException/死锁）分类返回 4xx 或 5xx，包装成新异常会丢掉这个分类能力
        {
            await tx.RollbackAsync();
            //入账失败必须留下可定位的堆栈
            _logger.LogError(ex,"订单 {OrderNo} 支付入账失败",outTradeNo);
            //保留原始异常类型向上抛：上层 ErrorText 需要按 DbUpdateException/死锁等类型决定是否翻译成 4xx
            throw;//NOSONAR 已记录完整堆栈，且必须保留原始异常类型供上层分类处理，不能包装或吞掉
        }
    }

    ///<summary>线上补缴单入账</summary>
    private async Task<(bool Ok,string Message)> ApplySupplementPaidAsync(string outTradeNo,string tradeNo,string totalAmount,string payType)
    {
        await using var tx=await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);

            //先按 out_trade_no 找到待支付的补缴记录（无锁读），拿到 payId 后再锁这一行
            var pending=await _db.PaymentRecords.FirstOrDefaultAsync(p=>p.OutTradeNo==outTradeNo&&p.BizType=="supplement"&&p.PayStatus=="待支付");
            if(pending==null)
            {
                await tx.CommitAsync();
                return(true,"补缴单不存在或已支付，忽略重复通知。");
            }

            var p=await DbRowLock.LockPaymentAsync(_db,pending.PayId);
            if(p==null||p.PayStatus!="待支付")
            {
                await tx.CommitAsync();
                return(true,"补缴单已处理，忽略重复通知。");
            }

            //金额核对：先严格解析，空串/非法值一律拒绝，再按补缴单应收金额 p.PayAmount 核对
            if(!PayNotifyGuard.TryParseAmount(totalAmount,out var paid))
            {
                _logger.LogWarning("补缴单 {OutTradeNo} 支付通知缺少合法金额（total_amount={Amount}），拒绝入账",outTradeNo,totalAmount);
                await tx.RollbackAsync();
                return(false,"支付通知缺少合法金额（total_amount），拒绝补缴入账。");
            }
            if(Math.Abs(paid-p.PayAmount) > 0.01m)
            {
                _logger.LogWarning("补缴单 {OutTradeNo} 金额不符：通知 {Paid}，应收 {Expect}",outTradeNo,paid,p.PayAmount);
                await tx.RollbackAsync();
                return(false,$"支付金额不符：通知 ¥{paid:0.00}，补缴应付 ¥{p.PayAmount:0.00}。");
            }

            p.MarkPaid(payType,DateTime.Now,tradeNo);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            //入账成功后通知顾客补缴成功（与退款/补缴通知共用顾客消息通道）
            var orderNo=await _db.RentalOrders.Where(o=>o.OrderId==p.OrderId).Select(o=>o.OrderNo).FirstOrDefaultAsync()??"";
            var cust=await _db.RentalOrders.Where(o=>o.OrderId==p.OrderId).Select(o=>o.CustomerId).FirstOrDefaultAsync();
            if(cust>0) await _messages.SendAsync(
                ("customer",cust),
                ("supplement_paid",orderNo),
                ("补缴成功通知",$"订单 {orderNo} 的逾期补缴 ¥{p.PayAmount:0.00} 已成功支付，感谢您的配合。"));

            _logger.LogInformation("补缴单 {OutTradeNo} 支付成功入账（{PayType}），金额 ¥{Amount}",outTradeNo,payType,p.PayAmount);
            return(true,$"补缴成功，订单 {orderNo} 补缴 ¥{p.PayAmount:0.00} 已收讫。");
        }
        catch(Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex,"补缴单 {OutTradeNo} 入账异常",outTradeNo);
            return(false,"补缴入账处理异常，请稍后重试。");
        }
    }

    ///<summary>支付宝接线自检：不依赖订单，直接向当前模式对应的网关发一次最小请求，判断配置是否可用</summary>
    public async Task<AlipaySelfCheckDto> SelfCheckAsync()
    {
        var rt=await _payConfig.ResolveAsync();
        var dto=new AlipaySelfCheckDto
        {
            Mode=rt.Mode,
            Enabled=rt.IsReal,
            Configured=rt.CredentialsComplete,
            AppId=rt.AppId,
            Gateway=rt.Gateway
        };

        if(rt.Mode==PayConfigService.ModeMock)
        {
            dto.Message="当前为「模拟」支付模式：未配置可用的沙箱/生产凭证，走内置模拟收银台（链路完整，无需联网）。"+"如需切换真实支付，请在下方填写沙箱或生产凭证。";
            return dto;
        }

        if(string.IsNullOrWhiteSpace(rt.PrivateKey))
        {
            dto.Message="未配置应用私钥";
            return dto;
        }

        //无论公私钥是否配对，先把本地私钥对应的「应用公钥」算出来，方便管理员核对
        try
        {
            dto.ExpectedAppPublicKey=AlipaySigner.DeriveApplicationPublicKey(rt.PrivateKey);
            dto.ExpectedAppPublicKeyFingerprint=AlipaySigner.PublicKeyFingerprint(dto.ExpectedAppPublicKey);
            dto.PrivateKeyFormat=AlipaySigner.DetectPrivateKeyFormat(rt.PrivateKey);
        }
        catch(Exception ex)
        {
            //只回异常类型名，不回 Message
            dto.AppPublicKeyError=$"私钥解析失败（{ex.GetType().Name}），请检查是否为完整的 PKCS#8 / PKCS#1 私钥";
            //模式判定只看"三个字段非空"
            dto.Configured=false;
            _logger.LogWarning(ex,"自检：由私钥推导应用公钥失败（{ExType}）",ex.GetType().Name);
        }

        try
        {
            //用一个必然不存在的订单号：只要签名通过，网关会回「交易不存在」，这恰恰证明「签名被接受」
            var parameters=BuildCommonParameters(rt,"alipay.trade.query",
                ToJson(new Dictionary<string,string?>{[AlipayParamOutTradeNo]="__SELFCHECK__"}));
            parameters["sign"]=AlipaySigner.Sign(AlipaySigner.BuildSignContent(parameters),rt.PrivateKey);

            var http=_httpFactory.CreateClient();
            http.Timeout=TimeSpan.FromSeconds(15);
            var resp=await http.PostAsync(rt.Gateway.TrimEnd('?')+"?charset=utf-8",new FormUrlEncodedContent(parameters.Where(kv=>kv.Value!=null).ToDictionary(kv=>kv.Key,kv=>kv.Value!)));
            var body=await ReadBodyAsync(resp);
            //先脱敏（清掉私钥/公钥字面值）再截断
            dto.RawResponse=SecretMasker.Truncate(SecretMasker.Scrub(body,rt.PrivateKey,rt.AlipayPublicKey),800);

            if(body.Contains("isv.invalid-signature"))
            {
                dto.SignatureAccepted=false;
                dto.Message="签名被沙箱拒绝：沙箱后台登记的「应用公钥」与你本地私钥不配对。"+$"请核对——本地私钥（{dto.PrivateKeyFormat}）对应的应用公钥指纹为 "+$"{dto.ExpectedAppPublicKeyFingerprint}，把它整段重新上传到沙箱「接口加签方式→自定义密钥→应用公钥」。";
            }
            else if(body.Contains("isv.missing-signature"))
            {
                dto.SignatureAccepted=false;
                dto.Message="沙箱未收到签名，检查请求构造";
            }
            else if(body.Contains("TRADE_NOT_EXIST")||body.Contains("40004")||body.Contains("\"code\":\"40002\""))
            {
                //40002 也可能含 ACQ.TRADE_NOT_EXIST 之类
                dto.SignatureAccepted=true;
                dto.Message="签名已被沙箱接受，配置正确（返回的业务错误属正常，因为订单号是探测用的假号）";
            }
            else
            {
                dto.SignatureAccepted=true;
                dto.Message="沙箱已响应且未报签名错误，配置可用";
            }
            return dto;
        }
        catch(Exception ex)
        {
            //异常对象只进服务端日志（保留堆栈）
            _logger.LogWarning(ex,"自检请求异常：{ExType} {Message}",ex.GetType().Name,
                SecretMasker.Scrub(ex.Message,rt.PrivateKey,rt.AlipayPublicKey));
            dto.Message=ex is TaskCanceledException or OperationCanceledException?"自检请求超时：网关在 15 秒内未响应，请检查网络或网关地址":$"自检请求异常（{ex.GetType().Name}），请检查网络与网关地址是否正确";
            return dto;
        }
    }

    ///<summary>主动查询支付宝交易状态（前端轮询+回跳兜底）</summary>
    public async Task QueryTradeStatusAsync(string orderNo)
    {
        //orderNo 有两种形态：租赁订单号（首付款）或补缴单 out_trade_no（SP- 前缀）
        var exists=orderNo.StartsWith("SP-")?await _db.PaymentRecords.AnyAsync(p=>p.OutTradeNo==orderNo):await _db.RentalOrders.AnyAsync(o=>o.OrderNo==orderNo);
        if(!exists) return;

        var rt=await _payConfig.ResolveAsync();
        if(!rt.IsReal||!rt.CredentialsComplete) return;//模拟模式：本地状态即真相

        string code,tradeStatus,tradeNo,totalAmount;
        try
        {
            (code,tradeStatus,tradeNo,totalAmount)=await QueryAlipayAsync(orderNo,rt);
        }
        catch(Exception ex)
        {
            //异常对象只进服务端日志
            _logger.LogWarning(ex,"查询支付宝订单 {OrderNo} 状态失败（{ExType}），回退本地状态",orderNo,ex.GetType().Name);
            return;//查询失败不影响本地状态，兜底
        }

        if(code!="10000")
        {
            if(code=="40004") return;
            //40002+isv.invalid-signature=密钥不配对
            _logger.LogWarning("查询订单 {OrderNo} 未成功：code={Code}（40004=交易不存在，40002=验签失败/参数错误）",orderNo,code);
            return;
        }

        if(tradeStatus is "TRADE_SUCCESS" or "TRADE_FINISHED")
        {
            //查单确认已付款
            var(ok,msg)=await ApplyTradeResultAsync(new Dictionary<string,string>
            {
                [AlipayParamOutTradeNo]=orderNo,
                [AlipayParamTradeStatus]=tradeStatus,
                [AlipayParamTradeNo]=tradeNo,
                [AlipayParamTotalAmount]=totalAmount
            },"支付宝");
            _logger.LogInformation("订单 {OrderNo} 查单兜底入账：{Result}（{Message}）",orderNo,ok?"成功":"拒绝",msg);
        }
    }

    ///<summary>会员购买（HY 开头交易号）的状态查询：不经过租赁订单表，直接向网关查单</summary>
    public async Task<(string Code,string TradeStatus,string TradeNo,string TotalAmount)> QueryTradeRawAsync(string outTradeNo)
    {
        var rt=await _payConfig.ResolveAsync();
        if(!rt.IsReal||!rt.CredentialsComplete) return("MOCK","","","");
        return await QueryAlipayAsync(outTradeNo,rt);
    }

    ///<summary>支付宝原路退款</summary>
    public async Task<(bool Ok,string Message)> RefundAsync(string outTradeNo,string? tradeNo,string outRequestNo,decimal amount,string? reason=null)
    {
        //退款定位：out_trade_no（商户订单号）与 trade_no（支付宝交易号）二选一
        if(string.IsNullOrWhiteSpace(outTradeNo)&&string.IsNullOrWhiteSpace(tradeNo)) return(false,"缺少支付宝订单号/交易号，无法原路退款；请在支付宝商户后台按订单号手工退款。");
        if(amount<=0) return(false,"退款金额必须大于 0。");

        var rt=await _payConfig.ResolveAsync();
        if(!rt.IsReal||!rt.CredentialsComplete) return(false,"当前为模拟支付模式，未发生真实资金划转。");

        var bizContent=new Dictionary<string,string?>
        {
            [AlipayParamOutTradeNo]=string.IsNullOrWhiteSpace(outTradeNo)?null:outTradeNo,
            [AlipayParamTradeNo]=string.IsNullOrWhiteSpace(tradeNo)?null:tradeNo,
            ["refund_amount"]=amount.ToString("0.00"),
            ["out_request_no"]=outRequestNo,
            ["refund_reason"]=string.IsNullOrWhiteSpace(reason)?null:SecretMasker.Truncate(reason.Trim(),200)
        };

        var parameters=BuildCommonParameters(rt,"alipay.trade.refund",ToJson(bizContent));
        parameters["sign"]=AlipaySigner.Sign(AlipaySigner.BuildSignContent(parameters),rt.PrivateKey);

        try
        {
            var http=_httpFactory.CreateClient();
            http.Timeout=TimeSpan.FromSeconds(15);
            var resp=await http.PostAsync(rt.Gateway.TrimEnd('?')+"?charset=utf-8",new FormUrlEncodedContent(parameters.Where(kv=>kv.Value!=null).ToDictionary(kv=>kv.Key,kv=>kv.Value!)));
            resp.EnsureSuccessStatusCode();
            var body=await ReadBodyAsync(resp);

            return ParseRefundResponse(body,outTradeNo,outRequestNo,amount);
        }
        catch(Exception ex)
        {
            //异常对象只进服务端日志
            _logger.LogWarning(ex,"支付宝退款请求异常：交易号 {TradeNo}，{ExType}",outTradeNo,ex.GetType().Name);
            return(false,ex is TaskCanceledException or OperationCanceledException?"支付宝退款请求超时，结果未知；请先在商户后台核实，切勿直接重复提交。":$"支付宝退款请求异常（{ex.GetType().Name}），结果未知；请先核实再重试。");
        }
    }

    ///<summary>解析 alipay.trade.refund 响应（退款结果判定逻辑集中在此，保持与主流程等价）</summary>
    private(bool Ok,string Message) ParseRefundResponse(string body,string outTradeNo,string outRequestNo,decimal amount)
    {
        using var doc=System.Text.Json.JsonDocument.Parse(body);
        if(!doc.RootElement.TryGetProperty("alipay_trade_refund_response",out var el)) return(false,"支付宝退款响应格式异常，请到商户后台核实退款状态后再重试。");

        string Get(string name)=>el.ValueKind==System.Text.Json.JsonValueKind.Object&&el.TryGetProperty(name,out var v)?v.GetString()??"":"";

        var code=Get("code");
        var fundChange=Get("fund_change");
        var refundFee=Get("refund_fee");

        if(code=="10000"&&(fundChange is "Y" or ""||!string.IsNullOrWhiteSpace(refundFee)))
        {
            _logger.LogInformation("支付宝退款受理成功：交易号 {TradeNo}，本次 ¥{Amount}，请求号 {RequestNo}",outTradeNo,amount,outRequestNo);
            return(true,$"支付宝已受理退款 ¥{amount:0.00}（请求号 {outRequestNo}）。");
        }

        //失败文案要用支付宝给的 msg/sub_msg（如「交易不存在」「退款金额大于可退金额」）
        var raw=$"{Get("msg")} {Get("sub_msg")}".Trim();
        var detail=string.IsNullOrWhiteSpace(raw)?$"code={code}":SecretMasker.Truncate(raw,200);
        _logger.LogWarning("支付宝退款未成功：交易号 {TradeNo}，code={Code}，原因 {Detail}",outTradeNo,code,detail);
        return(false,$"支付宝退款未成功：{detail}");
    }

    ///<summary>构造支付宝公共请求参数（app_id/method/format/charset/sign_type/timestamp/version/biz_content）</summary>
    private Dictionary<string,string?> BuildCommonParameters(PayRuntime rt,string method,string bizContent)=>new()
    {
        [AlipayParamAppId]=rt.AppId,
        [AlipayParamMethod]=method,
        [AlipayParamFormat]=AlipayFormatJson,
        [AlipayParamCharset]=AlipayCharsetUtf8,
        [AlipayParamSignType]=Options.SignType,
        //支付宝要求 timestamp 用本地时间 yyyy-MM-dd HH:mm:ss
        [AlipayParamTimestamp]=DateTime.Now.ToString(DateFormatMinute),
        [AlipayParamVersion]=AlipayVersionV1,
        [AlipayParamBizContent]=bizContent
    };

    ///<summary>向支付宝网关发起 alipay</summary>
    private async Task<(string Code,string TradeStatus,string TradeNo,string TotalAmount)> QueryAlipayAsync(string outTradeNo,PayRuntime rt)
    {
        var bizContent=ToJson(new Dictionary<string,string?>{[AlipayParamOutTradeNo]=outTradeNo});
        var parameters=BuildCommonParameters(rt,"alipay.trade.query",bizContent);
        parameters["sign"]=AlipaySigner.Sign(AlipaySigner.BuildSignContent(parameters),rt.PrivateKey);

        var http=_httpFactory.CreateClient();
        http.Timeout=TimeSpan.FromSeconds(10);
        //charset 必须作为 URL 查询串参数（支付宝网关要求）
        var resp=await http.PostAsync(rt.Gateway.TrimEnd('?')+"?charset=utf-8",new FormUrlEncodedContent(parameters.Where(kv=>kv.Value!=null).ToDictionary(kv=>kv.Key,kv=>kv.Value!)));
        resp.EnsureSuccessStatusCode();
        var body=await ReadBodyAsync(resp);

        //解析响应
        using var doc=System.Text.Json.JsonDocument.Parse(body);
        if(!doc.RootElement.TryGetProperty("alipay_trade_query_response",out var respEl))
        {
            respEl=doc.RootElement.GetProperty("alipay_trade_query").GetProperty("response");
            if(respEl.ValueKind==System.Text.Json.JsonValueKind.String) respEl=System.Text.Json.JsonDocument.Parse(respEl.GetString()??"{}").RootElement.Clone();
        }
        string Get(string name)=>respEl.ValueKind==System.Text.Json.JsonValueKind.Object&&respEl.TryGetProperty(name,out var v)?v.GetString()??"":"";
        return(Get("code"),Get(AlipayParamTradeStatus),Get(AlipayParamTradeNo),Get(AlipayParamTotalAmount));
    }

    ///<summary>构造查询串（值做 URL 编码，签名已完成）</summary>
    private static string BuildQueryString(IDictionary<string,string?> parameters)=>string.Join("&",parameters.Where(kv=>kv.Value!=null).Select(kv=>$"{kv.Key}={Uri.EscapeDataString(kv.Value!)}"));

    ///<summary>极简 JSON 构造</summary>
    private static string ToJson(IDictionary<string,string?> dict)
    {
        var sb=new StringBuilder("{");
        var first=true;
        foreach(var(k,v) in dict)
        {
            if(v==null) continue;
            if(!first) sb.Append(',');
            sb.Append('"').Append(k).Append("\":\"").Append(Escape(v)).Append('"');
            first=false;
        }
        return sb.Append('}').ToString();
    }

    private static string Escape(string s)=>s.Replace("\\","\\\\").Replace("\"","\\\"");

    ///<summary>读取支付宝网关响应体</summary>
    private static async Task<string> ReadBodyAsync(HttpResponseMessage resp)
    {
        var bytes=await resp.Content.ReadAsByteArrayAsync();
        var charset=resp.Content.Headers.ContentType?.CharSet?.Trim('"',' ');
        if(!string.IsNullOrEmpty(charset))
        {
            try{return Encoding.GetEncoding(charset).GetString(bytes);}
            catch{/* 编码名不被支持，落到下面的兜底 */}
        }
        try{return Encoding.GetEncoding("GBK").GetString(bytes);}
        catch{return Encoding.UTF8.GetString(bytes);}
    }
}

///<summary>支付通知入账前的公共防护：严格金额解析+进程内重放去重（支付宝/微信/银行卡回调共用）</summary>
internal static class PayNotifyGuard
{
    private static readonly TimeSpan Window=TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupInterval=TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<string,DateTime> Seen=new();
    private static long _lastCleanupTicks=DateTime.UtcNow.Ticks;

    ///<summary>严格解析金额：空串/空白/非法值一律失败</summary>
    public static bool TryParseAmount(string? text,out decimal value)
    {
        value=0m;
        return !string.IsNullOrWhiteSpace(text)
               && decimal.TryParse(text,NumberStyles.Number,CultureInfo.InvariantCulture,out value);
    }

    ///<summary>登记报文键：窗口内首次出现返回 false（放行），重复出现返回 true（应忽略）</summary>
    public static bool IsDuplicate(string key)
    {
        var now=DateTime.UtcNow;
        MaybeCleanup(now);

        //原子登记：并发下只有一个线程 TryAdd 成功，其余线程命中「窗口内已存在」被判重复
        if(Seen.TryAdd(key,now)) return false;
        if(Seen.TryGetValue(key,out var first)&&now-first<=Window) return true;
        //已过窗口的旧条目：刷新时间，按新一轮首次放行
        Seen[key]=now;
        return false;
    }

    ///<summary>把「通道+关键字段」拼成稳定键</summary>
    public static string BuildKey(string channel,params string?[] parts)
    {
        var raw=channel+"\u001f"+string.Join("\u001f",parts.Select(p=>p??""));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    ///<summary>按固定间隔清理过期条目</summary>
    private static void MaybeCleanup(DateTime now)
    {
        var last=Interlocked.Read(ref _lastCleanupTicks);
        if(now.Ticks-last<CleanupInterval.Ticks) return;
        //只让一个线程执行清理
        if(Interlocked.CompareExchange(ref _lastCleanupTicks,now.Ticks,last)!=last) return;
        foreach(var kv in Seen)
            if(now-kv.Value>Window) Seen.TryRemove(kv.Key,out _);
    }
}

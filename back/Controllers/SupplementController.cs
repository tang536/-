using System.Security.Claims;
using ERental.Authorization;
using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using ERental.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERental.Controllers;

/// <summary>
/// 线上补缴（逾期/损坏费用）支付与查询。
///
/// 背景：结算时若顾客不想当场交补缴款，门店选「线上补缴」会生成一张独立的待支付支付记录
/// （biz_type='supplement' + 全局唯一 out_trade_no）。本控制器让顾客回到顾客自助端后，
/// 用与原首付款一致的真实通道完成这笔补缴，并查询支付状态（含通道查单兜底）。
///
/// 安全：所有接口仅对顾客开放，且按 Token 内的 customerId 二次校验订单归属，不信任前端传参。
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = Policies.CustomerOnly)]
public class SupplementController(
    AppDbContext db, AlipayService alipay, WechatPayService wechat, BankPayService bank,
    PayConfigService payConfig, ILogger<SupplementController> logger) : ControllerBase
{
    private readonly AppDbContext _db = db;
    private readonly AlipayService _alipay = alipay;
    private readonly WechatPayService _wechat = wechat;
    private readonly BankPayService _bank = bank;
    private readonly PayConfigService _payConfig = payConfig;
    private readonly ILogger<SupplementController> _logger = logger;

    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    /// <summary>为某张待支付补缴单发起支付，复用支付宝/微信/银行卡三通道，返回收银台地址或二维码。</summary>
    [HttpPost("{payId:int}/pay")]
    public async Task<IActionResult> Pay(int payId, [FromBody] string? payTypeRaw)
    {
        try
        {
            var rec = await _db.PaymentRecords
                .FirstOrDefaultAsync(p => p.PayId == payId && p.BizType == "supplement");
            if (rec == null) return NotFound(new ApiResponse<object> { Code = 404, Message = "补缴单不存在", Success = false });

            var order = await _db.RentalOrders.FirstOrDefaultAsync(o => o.OrderId == rec.OrderId);
            if (order == null) return NotFound(new ApiResponse<object> { Code = 404, Message = "关联订单不存在", Success = false });
            if (order.CustomerId != CurrentUserId)
                return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "无权操作该补缴单", Success = false });
            if (rec.PayStatus != "待支付")
                return BadRequest(new ApiResponse<object> { Code = 400, Message = "该补缴单已支付，无需重复支付", Success = false });

            var payType = string.IsNullOrWhiteSpace(payTypeRaw) ? "支付宝" : payTypeRaw!.Trim();
            // 顾客选定渠道后写入记录（付款完成前门店侧也能看到真实意向）
            rec.PayType = payType;
            await _db.SaveChangesAsync();

            var rt = await _payConfig.ResolveAsync();
            var real = rt is { IsReal: true, CredentialsComplete: true };

            string payUrl = "", qrCode = "", payMode = "";
            bool realPay;
            switch (payType)
            {
                case "支付宝":
                    payUrl = await _alipay.CreatePagePayAsync(rec.OutTradeNo!, $"电动车租赁补缴 {order.OrderNo}", rec.PayAmount);
                    realPay = real;
                    break;
                case "微信":
                {
                    var wx = await _wechat.CreatePayAsync(
                        rec.OutTradeNo!, $"电动车租赁补缴 {order.OrderNo}", rec.PayAmount,
                        await BuildReturnUrl(rec.PayId));
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

            return Ok(new ApiResponse<CreatePayResultDto>
            {
                Data = new CreatePayResultDto
                {
                    OrderNo = order.OrderNo,
                    PayType = payType,
                    Amount = rec.PayAmount,
                    PayUrl = payUrl,
                    QrCode = qrCode,
                    PayMode = payMode,
                    RealPay = realPay
                }
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false });
        }
    }

    /// <summary>顾客查询自己的补缴单列表（含待支付/已支付），用于「我的订单 → 待补缴」展示。</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine()
    {
        try
        {
            var items = await _db.PaymentRecords
                .Where(p => p.BizType == "supplement"
                            && _db.RentalOrders.Any(o => o.OrderId == p.OrderId && o.CustomerId == CurrentUserId))
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
            return Ok(new ApiResponse<object> { Data = items });
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false });
        }
    }

    /// <summary>
    /// 查询某张补缴单支付状态：若仍为待支付，按所选通道主动查单兜底（应对回调延迟/丢包）；
    /// 返回 Paid 供前端轮询终止。
    /// </summary>
    [HttpGet("{payId:int}/status")]
    public async Task<IActionResult> Status(int payId)
    {
        try
        {
            var rec = await _db.PaymentRecords
                .FirstOrDefaultAsync(p => p.PayId == payId && p.BizType == "supplement");
            if (rec == null) return NotFound(new ApiResponse<object> { Code = 404, Message = "补缴单不存在", Success = false });

            var order = await _db.RentalOrders.FirstOrDefaultAsync(o => o.OrderId == rec.OrderId);
            if (order == null) return NotFound(new ApiResponse<object> { Code = 404, Message = "关联订单不存在", Success = false });
            if (order.CustomerId != CurrentUserId)
                return StatusCode(403, new ApiResponse<object> { Code = 403, Message = "无权查看该补缴单", Success = false });

            if (rec.PayStatus == "待支付")
            {
                var type = rec.PayType ?? "支付宝";
                try
                {
                    if (type == "微信") await _wechat.QueryTradeStatusAsync(rec.OutTradeNo!);
                    else await _alipay.QueryTradeStatusAsync(rec.OutTradeNo!);
                    await _db.Entry(rec).ReloadAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "补缴单 {PayId} 查单兜底失败，回退本地状态", payId);
                }
            }

            return Ok(new ApiResponse<object>
            {
                Data = new
                {
                    rec.PayId,
                    order.OrderNo,
                    rec.PayAmount,
                    rec.PayType,
                    rec.PayStatus,
                    Paid = rec.PayStatus == "已支付"
                }
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false });
        }
    }

    /// <summary>构建支付完成后回跳的前端地址（微信 H5 的 redirect_url 等），带上补缴单号。</summary>
    private async Task<string> BuildReturnUrl(int payId)
    {
        var raw = await _payConfig.GetRawAsync();
        var configured = raw.TryGetValue(PayConfigService.KeyReturnUrl, out var v) ? v.Trim() : "";
        var baseUrl = _payConfig.FallbackChannelUrl(configured, $"supplement-pay/{payId}");
        return baseUrl;
    }
}

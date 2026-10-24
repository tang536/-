using System.Security.Claims;
using ERental.Authorization;
using ERental.Models;
using ERental.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERental.Controllers;

/// <summary>
/// 顾客侧消息中心：统一走通用 message 表（receiver_type='customer'）。
/// 与员工侧共用同一张表与同一套已读语义——员工侧入口仍由 ReportController 提供。
/// 所有操作按 Token 内的 customerId 归属过滤，不信任前端传参。
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = Policies.CustomerOnly)]
public class MessageController(MessageService messages, ILogger<MessageController> logger) : ControllerBase
{
    private readonly MessageService _messages = messages;
    private readonly ILogger<MessageController> _logger = logger;

    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    /// <summary>顾客查询自己的消息列表（倒序）与未读计数。</summary>
    [HttpGet("customer")]
    public async Task<IActionResult> List()
    {
        try
        {
            var result = await _messages.ListAsync("customer", CurrentUserId);
            return Ok(new ApiResponse<object> { Data = result });
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false });
        }
    }

    /// <summary>标记已读：body 传 ids；不传或空数组表示「全部标记已读」。</summary>
    [HttpPost("customer/read")]
    public async Task<IActionResult> MarkRead([FromBody] MessageIdsRequest? req)
    {
        try
        {
            await _messages.MarkReadAsync("customer", CurrentUserId, req?.Ids);
            return Ok(new ApiResponse<object> { Message = "已标记已读" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "顾客 {UserId} 标记消息已读失败", CurrentUserId);
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false });
        }
    }

    /// <summary>
    /// 删除消息：body 传 ids 删除指定消息；不传或空数组表示「清空我的全部消息」。
    /// 删除范围按 (customer, 当前用户) 限定，不会越权删到他人消息。
    /// </summary>
    [HttpPost("customer/delete")]
    public async Task<IActionResult> Delete([FromBody] MessageIdsRequest? req)
    {
        try
        {
            var n = await _messages.DeleteAsync("customer", CurrentUserId, req?.Ids);
            return Ok(new ApiResponse<object> { Message = n > 0 ? $"已删除 {n} 条消息" : "没有可删除的消息" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "顾客 {UserId} 删除消息失败", CurrentUserId);
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false });
        }
    }
}

/// <summary>消息操作请求体：ids 为消息主键列表，为空表示「全部」。</summary>
public class MessageIdsRequest
{
    public List<int>? Ids { get; set; }
}

using System.Security.Claims;
using ERental.DTOs;
using ERental.Filters;
using ERental.Models;
using ERental.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERental.Controllers;

/// <summary>消息中心：员工通知与顾客消息共用同一套接口、同一张 message 表</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MessageController(
    MessageService messages,
    IEnumerable<INotificationSource> sources,
    ILogger<MessageController> logger) : ControllerBase
{
    private readonly MessageService _messages = messages;
    private readonly IEnumerable<INotificationSource> _sources = sources;
    private readonly ILogger<MessageController> _logger = logger;

    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    /// <summary>员工看 staff 队列、顾客看 customer 队列——同一个 Token 只会落到其中一边</summary>
    private string ReceiverType => User.IsInRole(AuthService.RoleCustomer) ? "customer" : "staff";

    private async Task SyncSourcesAsync()
    {
        // 同步是「尽力而为」的旁路：预警推导失败不该让整个消息面板打不开，
        // 记录后继续读表，用户至少能看到已有的消息。
        foreach (var source in _sources)
        {
            try
            {
                await source.SyncAsync(ReceiverType, CurrentUserId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "同步通知来源失败（{ReceiverType}/{UserId}）", ReceiverType, CurrentUserId);
            }
        }
    }

    /// <summary>查询自己的消息列表（倒序分页）与未读计数</summary>
    [HttpGet("list")]
    public async Task<IActionResult> ListNotifications([FromQuery] int page = 1, [FromQuery] int pageSize = MessageService.DefaultPageSize)
    {
        try
        {
            await SyncSourcesAsync();
            var result = await _messages.ListAsync(ReceiverType, CurrentUserId, page, pageSize);
            return Ok(new ApiResponse<NotificationListDto> { Data = result });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取消息列表失败（{ReceiverType}/{UserId}）", ReceiverType, CurrentUserId);
            return BadRequest(new ApiResponse<NotificationListDto> { Code = 400, Message = ErrorText.Safe(ex), Success = false });
        }
    }

    /// <summary>标记已读：body 传 ids</summary>
    [HttpPost("read")]
    [SkipAudit]   // 已读状态属个人阅读偏好，非业务数据变更；高频写日志会淹没真正的业务操作
    public async Task<IActionResult> MarkRead([FromBody] NotificationReadRequest? req)
    {
        try
        {
            await _messages.MarkReadAsync(ReceiverType, CurrentUserId, req?.Ids);
            return Ok(new ApiResponse<object> { Message = "已标记已读" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "标记消息已读失败（{ReceiverType}/{UserId}）", ReceiverType, CurrentUserId);
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false });
        }
    }

    /// <summary>一键全部已读：与 同一条路径，等价于 ids 为空</summary>
    [HttpPost("read-all")]
    [SkipAudit]
    public async Task<IActionResult> MarkAllRead()
    {
        try
        {
            await _messages.MarkReadAsync(ReceiverType, CurrentUserId, null);
            return Ok(new ApiResponse<object> { Message = "已全部标记为已读" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "全部已读失败（{ReceiverType}/{UserId}）", ReceiverType, CurrentUserId);
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false });
        }
    }

    /// <summary>删除（忽略）消息：body 传 ids 删除指定消息</summary>
    [HttpPost("delete")]
    [SkipAudit]
    public async Task<IActionResult> DeleteNotification([FromBody] NotificationReadRequest? req)
    {
        try
        {
            var n = await _messages.DismissAsync(ReceiverType, CurrentUserId, req?.Ids);
            return Ok(new ApiResponse<object> { Message = n > 0 ? $"已删除 {n} 条消息" : "没有可删除的消息" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除消息失败（{ReceiverType}/{UserId}）", ReceiverType, CurrentUserId);
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false });
        }
    }
}

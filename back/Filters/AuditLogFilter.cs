using System.Security.Claims;
using ERental.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ERental.Filters;

/// <summary>
/// 标记「本接口不参与自动审计」。
///
/// 适用两类情况：
///  ① 已有更精细的手工审计（如 PaymentController 的 <c>AuditAsync</c>，会记录生效模式、
///     订单号、失败原因等上下文），再自动记一条只会重复；
///  ② 高频且无追溯价值的操作（如通知「已读」、ComfyUI 连通性测试），记了会淹没真正重要的改动。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SkipAuditAttribute : Attribute;

/// <summary>
/// 关键写操作自动审计（需求规格说明书 4.2.1「日志管理」、7.1「数据不可删除、全流程可追溯」）。
///
/// 需求原文要求「系统**自动**记录全量用户登录、操作、数据修改、异常行为日志」。
/// 改造前只有登录与支付两处手工写日志，订单/车辆/客户/结算等真正的数据变更全部没有留痕——
/// 一旦出现「谁把订单取消了」「谁把这台车报废了」，无从追溯。这里用全局过滤器补齐：
///   · 拦截范围：POST / PUT / DELETE / PATCH 四类写方法，GET 只读不记（否则日志表会被查询淹没）；
///   · 记录内容：模块 + 真实请求路径 + 结果。用真实路径而不是路由模板，
///     因为模板 `api/vehicle/{id:int}/status` 不含主键，事后无法定位改的是哪台车；
///   · 不记录请求体：其中可能含密码、身份证号、支付凭据，`operate_content` 只有 200 字符，
///     也不适合塞参数快照；需要细节时按路径 + 时间回查业务表即可；
///   · 写日志失败绝不影响业务：审计是旁路动作，用独立作用域 + try/catch，失败只打警告。
///
/// 与表结构的关系：完全复用既有 <c>system_log</c> 七个字段
/// （log_id / user_id / operate_content / operate_time / operate_result / operate_module / ip_address），
/// **不新增列**，保持与《唐荣强.docx》图4-15 的 17 张表 134 字段一致。
/// </summary>
public sealed class AuditLogFilter(
    IServiceScopeFactory scopeFactory,
    ILogger<AuditLogFilter> logger) : IAsyncActionFilter
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<AuditLogFilter> _logger = logger;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        var metadata = context.ActionDescriptor.EndpointMetadata;

        // 只读请求、显式匿名接口（登录/注册/支付回调）、显式豁免的接口，一律直接放行。
        // 匿名接口不做自动审计的理由：登录已由 AuthService 按「失败原因」逐条精细记录，
        // 支付回调已由 PaymentController.AuditAsync 记录订单号与入账结果，自动再记一条是重复。
        if (!AuditModules.IsWriteMethod(request.Method)
            || metadata.OfType<IAllowAnonymous>().Any()
            || metadata.OfType<SkipAuditAttribute>().Any())
        {
            await next();
            return;
        }

        var executed = await next();

        // 审计是旁路：任何失败都只写服务端日志，绝不让业务请求因此报错。
        try
        {
            var http = executed.HttpContext;
            var failedByException = executed.Exception is not null;
            var statusCode = failedByException ? StatusCodes.Status500InternalServerError : StatusOf(executed.Result);
            var success = !failedByException && statusCode < 400;

            var controller = context.ActionDescriptor.RouteValues.TryGetValue("controller", out var c) ? c : null;
            var module = AuditModules.Resolve(controller);

            var rawId = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            int? userId = int.TryParse(rawId, out var id) ? id : null;

            // 内容控制在 200 字符内（system_log.operate_content 的长度）：
            // 模块 + 方法 + 路径，路径已含主键，足以定位操作对象。
            var content = $"{module}：{request.Method} {request.Path}";
            if (failedByException) content += "（处理异常）";
            else if (statusCode >= 400) content += $"（返回 {statusCode}）";
            if (content.Length > 200) content = content[..200];

            using var scope = _scopeFactory.CreateScope();
            var logs = scope.ServiceProvider.GetRequiredService<LogService>();
            await logs.WriteLog(userId, content, module, success ? "成功" : "失败", ClientIp(http));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写操作审计落库失败（不影响业务）：{Method} {Path}",
                request.Method, request.Path);
        }
    }

    /// <summary>取真实响应状态码；未显式设置时按 200 处理（MVC 的返回值最终会被包装成 200）。</summary>
    private static int StatusOf(IActionResult? result) => result switch
    {
        ObjectResult o => o.StatusCode ?? StatusCodes.Status200OK,
        StatusCodeResult s => s.StatusCode,
        _ => StatusCodes.Status200OK
    };

    /// <summary>
    /// 客户端 IP：优先取 X-Forwarded-For 首个地址（反代/隧道场景），否则用连接远端地址。
    /// 与 PaymentController 的取法保持一致，避免同一系统里两种口径。
    /// </summary>
    private static string? ClientIp(HttpContext http)
    {
        var forwarded = http.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();
            if (first.Length > 0) return first;
        }
        return http.Connection.RemoteIpAddress?.ToString();
    }
}

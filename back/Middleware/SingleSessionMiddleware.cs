using System.Security.Claims;
using ERental.Models;
using ERental.Services;

namespace ERental.Middleware;

/// <summary>
/// 单点登录校验（同一账号不允许同时登录）。
///
/// 位置很关键：必须挂在 <c>UseAuthentication()</c> **之后**、<c>UseAuthorization()</c> **之前**。
///   · 之前——JWT 中间件还没把 token 解析成 ClaimsPrincipal，读不到 sid 声明；
///   · 授权之前——本中间件对所有受保护接口统一生效，16 个控制器无需逐个改造，
///     将来新增的控制器也自动受保护，不会出现「新加的接口忘了加单点登录校验」的漏洞。
///
/// 判定规则：已通过 JWT 认证的请求，其 sid 必须是该账号在 <see cref="SessionRegistry"/> 里的
/// 当前会话，否则返回 401（前端据此清理登录态并提示「已在其他设备登录」）。
/// 匿名请求（登录、注册、支付回跳页、静态资源）没有 ClaimsPrincipal，直接放行。
/// </summary>
public sealed class SingleSessionMiddleware(RequestDelegate next)
{
    /// <summary>JWT 中的会话标识声明名（与 AuthService.GenerateJwt 写入的一致）</summary>
    public const string SessionClaim = "sid";

    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context, SessionRegistry registry)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var sid = context.User.FindFirst(SessionClaim)?.Value;
            var rawId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var userType = context.User.FindFirst("user_type")?.Value ?? "staff";

            // 解析单独成句：写在 && 短路表达式里的话，userId 在后续使用时不算"确定赋值"（CS0165）
            var hasId = int.TryParse(rawId, out var userId);
            // 一次查询同时拿到「是否当前会话」与「撤销原因」，避免每个请求打两次库
            (bool Online, string? Reason) state = sid is not null && hasId ? await registry.CheckAsync(userType, userId, sid) : (false, null);
            var online = state.Online;

            if (!online)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(new ApiResponse<object>
                {
                    Code = 401,
                    Success = false,
                    // 三种失效原因分开报，用户和排查的人都一眼看得懂：
                    //   · 没有 sid        → 老版本登录态，需要重新登录一次来绑定会话
                    //   · 会话被强制撤销  → 用撤销原因（被停用 / 改了密码 / 账号注销…）
                    //   · 其余            → 被别处的登录顶下线
                    Message = sid is null
                        ? "登录状态已更新，请重新登录"
                        : state.Reason ?? "该账号已在其他设备登录，当前登录已被下线"
                });
                return;
            }
        }

        await _next(context);
    }
}

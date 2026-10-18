using ERental.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace ERental.Authorization;

/// <summary>
/// 授权失败的统一响应体（401 / 403 都返回和业务接口一致的 <see cref="ApiResponse{T}"/> 结构）。
///
/// 为什么需要替换框架默认行为：
///   策略校验失败时，AuthorizationMiddleware 默认只置状态码、body 为空。
///   前端拦截器读不到 message，只能显示「当前角色无权执行该操作」这类通用兜底文案，
///   用户不知道自己到底是**没登录**、**登录过期**，还是**角色不够**——三种情况处理动作完全不同。
///   统一成 JSON 后，前端可以原样展示后端给的原因，也便于排查越权请求。
///
/// 两种失败的语义区分（沿用 ASP.NET Core 的定义）：
///   · <c>Challenged</c>：没有任何有效身份 → 401，前端应走「续期或重新登录」；
///   · <c>Forbidden</c>：身份有效但策略不满足 → 403，重新登录也没用，属于权限配置问题。
/// </summary>
public sealed class JsonAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    /// <summary>框架默认处理器：策略通过时交回给它，保持正常请求的处理链路不变</summary>
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded)
        {
            await _default.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        var status = authorizeResult.Challenged
            ? StatusCodes.Status401Unauthorized
            : StatusCodes.Status403Forbidden;

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new ApiResponse<object>
        {
            Code = status,
            Success = false,
            Message = status == StatusCodes.Status401Unauthorized
                ? "登录已失效，请重新登录"
                : "当前角色无权执行该操作"
        });
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ERental.Authorization;

/// <summary>
/// 全局授权兜底（fail-closed）：任何既没标 <see cref="IAllowAnonymous"/>、
/// 也没有 <see cref="IAuthorizeData"/> 的 action，一律拒绝。
///
/// 为什么需要这一层——框架的默认行为是「没标 [Authorize] 就放行」：
///   本项目 20 个控制器、80 处标注，靠人工保证「每个新接口都记得加授权」并不可靠。
///   漏标一处就是一个匿名可访问的接口，而且**不会有任何报错**——
///   属于最难在测试阶段被发现、最容易在答辩/上线时被指出的越权隐患。
///   加上这一层后，漏标会立刻变成 401/403，本地一跑就能发现，问题从「静默」变成「显性」。
///
/// 与需求规格说明书的对应关系：
///   4.2.1 角色权限管理「各角色仅可进入其权限范围内的功能模块，实现权限分级管控」；
///   7.1 安全保密要求「不同角色用户仅可查看、操作权限范围内的功能与数据，禁止越权操作」。
///   兜底过滤器保证的是「授权判定一定存在」，具体判定内容仍由各 action 上的策略决定。
///
/// 实现要点：
///   · 用 <c>IAsyncAuthorizationFilter</c> 而非中间件——只有它能在 MVC 选中 action 之后拿到
///     <c>EndpointMetadata</c>，从而看到类级 + 方法级的全部特性；
///   · 未认证返回 401（前端据此走登录/刷新流程），已认证但策略未声明返回 403。
/// </summary>
public sealed class RequireAuthorizationFilter(ILogger<RequireAuthorizationFilter> logger)
    : IAsyncAuthorizationFilter
{
    private readonly ILogger<RequireAuthorizationFilter> _logger = logger;

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var metadata = context.ActionDescriptor.EndpointMetadata;

        // 显式匿名（登录、注册、支付回调）与显式授权（策略或角色）都算「已作决策」。
        var decided = metadata.OfType<IAllowAnonymous>().Any()
                      || metadata.OfType<IAuthorizeData>().Any();
        if (decided) return Task.CompletedTask;

        var action = context.ActionDescriptor.DisplayName ?? context.HttpContext.Request.Path;
        var user = context.HttpContext.User;
        var authenticated = user.Identity?.IsAuthenticated == true;

        // 走到这里说明这个接口没有任何授权声明——属于开发期就该修掉的疏漏，记 Warning 便于排查。
        _logger.LogWarning(
            "接口 {Action} 未声明任何授权特性，已被全局兜底过滤器拒绝（{Method} {Path}，已认证={Auth}）",
            action, context.HttpContext.Request.Method, context.HttpContext.Request.Path, authenticated);

        context.Result = new ObjectResult(new
        {
            code = authenticated ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized,
            success = false,
            message = authenticated
                ? "该接口未配置访问策略，已按最小权限原则拒绝访问"
                : "请先登录后再访问该接口"
        })
        {
            StatusCode = authenticated
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized
        };

        return Task.CompletedTask;
    }
}

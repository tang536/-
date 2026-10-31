using ERental.Services;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ERental.Filters;

/// <summary>
/// 全局入参 XSS 清洗（模型绑定之后、动作方法之前执行）。
/// 挂在 <see cref="Microsoft.Extensions.DependencyInjection.MvcCoreMvcBuilderExtensions"/> 的
/// 全局过滤器上，17 个控制器、100 多个接口一次性覆盖，
/// 不需要（也不允许）由各控制器自己记得调用——「靠自觉」的安全措施等于没有。
/// 只清不改语义：<see cref="InputSanitizer.Clean"/> 剥离的是 HTML 标签与脚本伪协议，
/// 普通文本（含「价格&lt;100」这类尖括号）原样保留。
/// 改动内容会写一条 Warning 日志（含路径与处数）：谁在往备注里粘 HTML，
/// 事后排查运营问题时有据可查；不记原值，避免把可能敏感的内容再落一份。
/// </summary>
public sealed class XssGuardFilter(ILogger<XssGuardFilter> logger) : IActionFilter
{
    private readonly ILogger<XssGuardFilter> _logger = logger;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var changed = 0;
        // Keys 在遍历中不可变，但值会被替换，故先取一份键快照
        foreach (var key in context.ActionArguments.Keys.ToList())
        {
            var arg = context.ActionArguments[key];

            // 裸字符串参数（[FromQuery] string keyword 这类）是值类型绑定，
            // 清洗结果必须写回字典才会传给动作方法——改对象属性对它是无效的
            if (arg is string raw)
            {
                var cleaned = InputSanitizer.Clean(raw);
                if (!ReferenceEquals(cleaned, raw))
                {
                    context.ActionArguments[key] = cleaned;
                    changed++;
                }
                continue;
            }

            changed += InputSanitizer.SanitizeObject(arg);
        }

        if (changed > 0)
        {
            _logger.LogWarning("入参含 HTML 标签或脚本伪协议，已剥离 {Count} 处：{Method} {Path}", changed, context.HttpContext.Request.Method, context.HttpContext.Request.Path);
        }
    }

    /// <inheritdoc/>
    public void OnActionExecuted(ActionExecutedContext context) { }
}

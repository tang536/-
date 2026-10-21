using System.Net;
using System.Security.Claims;
using System.Text.Json;
using ERental.Services;

namespace ERental.Middleware;

/// <summary>
/// 全局异常兜底 + **对外响应脱敏**。
///
/// 三类异常的处理口径：
///  ① 业务校验异常（我方主动 throw 的中文提示）→ 原样返回，管理员/用户需要知道原因；
///  ② 数据库异常 → 翻译成中文提示（原始英文报错进日志）；
///  ③ 其余未预期异常 → 只回**通用中文提示**，**绝不回 ex.Message**。
///
/// ⚠️ 为什么 ③ 不能回原始 Message（本轮安全审查的重点）：
///  未预期异常的 Message 常常暴露实现细节——连接字符串、文件绝对路径、
///  网关原始响应、甚至签名/密钥片段；而堆栈里还可能带请求参数。
///  这些一旦下发到浏览器，等于把内部结构白送给对方。因此：
///    - 响应体只给「服务内部错误，请稍后重试」；
///    - 真实异常（含堆栈）**只写服务端日志**；
///    - 若要在日志里附异常消息，必须先过 <see cref="SecretMasker.Scrub"/> 清掉已知敏感值。
/// </summary>
public class ExceptionMiddleware(
    RequestDelegate next,
    ILogger<ExceptionMiddleware> logger,
    IServiceScopeFactory scopeFactory)
{
    private readonly RequestDelegate _next = next;
    private readonly ILogger<ExceptionMiddleware> _logger = logger;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            // 完整异常（含堆栈）只进日志。业务异常与并发冲突按 Information 记，
            // 它们是预期内的用户/并发问题，不该污染 Error 级别。
            var isBusiness = ErrorText.IsBusiness(ex) || ex is ConcurrentOperationException;
            if (isBusiness)
                _logger.LogInformation("请求 {Method} {Path} 业务校验未通过：{Message}",
                    context.Request.Method, context.Request.Path, ex.Message);
            else
                _logger.LogError(ex, "请求 {Method} {Path} 处理失败（{ExType}）",
                    context.Request.Method, context.Request.Path, ex.GetType().Name);

            // 需求 4.2.1 / 8.3「所有故障、异常、报错信息均自动记录至系统日志」：
            // 只记**非业务异常**——业务校验失败是预期内的用户输入问题（前端已即时提示），
            // 全量入库会把日志表灌满「密码错误」「余额不足」这类噪音，掩盖真正的故障。
            // 写方法不在这里记：它们已由 AuditLogFilter 以「（处理异常）」标注入库，避免同一请求两条。
            if (!isBusiness && !AuditModules.IsWriteMethod(context.Request.Method))
                await WriteFaultLogAsync(context, ex);

            // 文案统一交给 ErrorText：业务中文原样、数据库异常分类翻译、其余走通用文案。
            // 控制器层那 68 处 catch 现在调用的是同一个方法，两条出口口径一致。
            var safeMessage = ErrorText.Safe(ex);

            var status = isBusiness || ErrorText.DescribeDatabase(ex) != null
                ? (int)HttpStatusCode.BadRequest
                : (int)HttpStatusCode.InternalServerError;

            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.StatusCode = status;
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                code = status,
                message = safeMessage,
                success = false
                // 注意：不返回 stackTrace / innerException / traceId 等排查线索给前端。
                // 需要定位时请查服务端日志（那里有完整堆栈）。
            }));
        }
    }

    /// <summary>
    /// 系统级故障写入 <c>system_log</c>（需求规格 4.2.1「所有故障、异常、报错信息均自动记录」）。
    ///
    /// 只写异常**类型名**（如 MySqlException），不写 Message：
    /// Message 可能含连接串、绝对路径、网关原始响应等敏感片段，而 system_log 是可在界面上查看的表。
    /// 记录本身失败时静默降级——审计是旁路，不能把「原始故障」覆盖成「记录故障时又出错」。
    /// </summary>
    private async Task WriteFaultLogAsync(HttpContext context, Exception ex)
    {
        try
        {
            var module = AuditModules.ResolveFromPath(context.Request.Path);
            var rawId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            int? userId = int.TryParse(rawId, out var id) ? id : null;

            var content = $"{module}接口异常：{context.Request.Method} {context.Request.Path}（{ex.GetType().Name}）";
            if (content.Length > 200) content = content[..200];

            using var scope = _scopeFactory.CreateScope();
            var logs = scope.ServiceProvider.GetRequiredService<LogService>();
            await logs.WriteLog(userId, content, module, "失败");
        }
        catch (Exception logEx)
        {
            _logger.LogWarning(logEx, "系统故障日志落库失败（不影响主流程）");
        }
    }
}

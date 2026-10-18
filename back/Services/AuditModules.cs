namespace ERental.Services;

/// <summary>
/// 操作日志的模块口径与写方法判定 —— 自动审计过滤器与异常中间件共用。
///
/// 单独抽出来的原因：两处都要把「控制器名」翻译成需求里的功能模块名、
/// 都要判断「这是不是一次数据变更」。各写一份的话，模块名一旦对不上，
/// 日志页按模块筛选就会出现「同一件事分成两个模块」的脏数据。
/// </summary>
public static class AuditModules
{
    /// <summary>需要留痕的写方法。GET/HEAD/OPTIONS 是查询，不计入操作日志。</summary>
    private static readonly HashSet<string> WriteMethods =
        new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "DELETE", "PATCH" };

    /// <summary>
    /// 控制器名 → 需求 4.2.1 与《唐荣强.docx》结构图里的功能模块名。
    /// 与前端日志页的「操作模块」下拉同源，保证筛选口径一致。
    /// </summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["AiImage"] = "AI 生图",
        ["Auth"] = "用户登录",
        ["Check"] = "车辆盘点",
        ["Config"] = "系统配置",
        ["Customer"] = "客户管理",
        ["Log"] = "日志管理",
        ["Maintenance"] = "维保管理",
        ["Member"] = "会员管理",
        ["Order"] = "订单管理",
        ["Payment"] = "支付管理",
        ["Print"] = "打印导出",
        ["Renewal"] = "续租管理",
        ["Report"] = "统计报表",
        ["Settlement"] = "结算管理",
        ["User"] = "用户管理",
        ["Vehicle"] = "车辆管理"
    };

    /// <summary>请求方法是否属于需要留痕的写操作</summary>
    public static bool IsWriteMethod(string method) => WriteMethods.Contains(method);

    /// <summary>控制器名 → 中文模块名；未知控制器回退原始名</summary>
    public static string Resolve(string? controller) =>
        string.IsNullOrEmpty(controller) ? "系统" : Names.GetValueOrDefault(controller, controller);

    /// <summary>从请求路径里取控制器段：<c>/api/vehicle/12/status</c> → <c>vehicle</c></summary>
    private static string? ControllerOf(PathString path)
    {
        var seg = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // 形如 api/{controller}/{...}，段数不足说明不是业务接口
        return seg is { Length: >= 2 } && seg[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            ? seg[1]
            : null;
    }

    /// <summary>把路径里的控制器段翻译成中文模块名</summary>
    public static string ResolveFromPath(PathString path) => Resolve(ControllerOf(path));
}

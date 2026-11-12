using Microsoft.AspNetCore.Authorization;

namespace ERental.Authorization;

/// <summary>
/// 系统角色名常量。
/// 为什么要把中文字符串收敛成常量：
///   `[Authorize(Roles = "管理员")]` 这种写法把角色名硬编码在 20 个控制器的 80 处标注里，
///   一旦角色名调整（例如「前台」改叫「门店前台」）就必须全局搜索替换，漏一处就是静默越权或误拒。
///   角色名与数据库 `role.role_name`、前端 `ROUTE_ROLES` 必须完全一致，
///   集中在此处定义后，改名只需改一行，编译期即可发现引用点。
/// </summary>
public static class RoleNames
{
    public const string Admin = "管理员";
    public const string Reception = "前台";
    public const string Finance = "财务";
    public const string Customer = "顾客";
}

/// <summary>
/// 授权策略名。
/// 策略内容 = 需求规格说明书 4.2.1「角色权限管理」与《唐荣强.docx》用况1~5 的角色分工：
///   用况1 注册登录、用况2 创建订单、用况3 归还结算、用况4 车辆与维保、用况5 统计报表。
///   策略只在既有角色组合上做**等价命名**，不新增角色、不改变任何接口的可访问范围。
/// 与直接写 `Roles = "管理员,前台"` 相比的收益：
///   ① 授权规则从「散落 80 处的字符串」变成「12 条具名规则」，一眼能看出全系统有几档权限；
///   ② 将来要加细粒度规则（例如「仅本人的订单」）时，只需在策略里加 <c>RequireAssertion</c>，
///      不必逐个控制器改造——这正是 <see cref="AuthorizationSetup"/> 存在的意义；
///   ③ 拼写错误从「运行时静默放行/拒绝」提前到编译期报错。
/// </summary>
public static class Policies
{
    /// <summary>仅管理员：系统配置、账号管理、数据字典、车辆与维保、盘点、AI 生图</summary>
    public const string AdminOnly = "AdminOnly";

    /// <summary>管理员或前台：订单、客户档案、车辆查询与状态流转</summary>
    public const string AdminOrReception = "AdminOrReception";

    /// <summary>管理员或财务：财务报表、结算查询</summary>
    public const string AdminOrFinance = "AdminOrFinance";

    /// <summary>管理员或前台或财务：统计报表总览（三类员工都看得到经营数据）</summary>
    public const string AnyStaff = "AnyStaff";

    /// <summary>管理员或顾客：顾客注销（管理员代为操作、顾客本人操作）</summary>
    public const string AdminOrCustomer = "AdminOrCustomer";

    /// <summary>仅前台：线下开单、归还、结算办理</summary>
    public const string ReceptionOnly = "ReceptionOnly";

    /// <summary>前台或财务：小票与结算单打印</summary>
    public const string ReceptionOrFinance = "ReceptionOrFinance";

    /// <summary>仅财务：财务口径报表与明细导出</summary>
    public const string FinanceOnly = "FinanceOnly";

    /// <summary>仅顾客：本人的订单、会员、档案</summary>
    public const string CustomerOnly = "CustomerOnly";

    /// <summary>管理员或前台或顾客：车辆目录浏览、订单小票自助打印</summary>
    public const string AdminOrReceptionOrCustomer = "AdminOrReceptionOrCustomer";

    /// <summary>四类角色皆可：支付下单与回跳。语义上等同「已登录」，但保留角色白名单以防将来新增角色被默许</summary>
    public const string AnyRole = "AnyRole";

    /// <summary>任意已登录用户，不限角色（对应裸 <c>[Authorize]</c>）</summary>
    public const string AnyAuthenticated = "AnyAuthenticated";
}

/// <summary>
/// 授权策略注册。
/// 放在独立的静态类里而不是堆进 Program.cs：Program.cs 已承担启动编排，
/// 授权规则属于「安全策略」这一单一职责，独立后便于对照需求文档逐条核对。
/// </summary>
public static class AuthorizationSetup
{
    /// <summary>把 12 条策略注册进授权服务</summary>
    public static void Configure(AuthorizationOptions options)
    {
        // 兜底要求：所有策略都建立在「已认证」之上。匿名访问必须显式标注 [AllowAnonymous]。
        options.AddPolicy(Policies.AdminOnly, p => p.RequireRole(RoleNames.Admin));
        options.AddPolicy(Policies.AdminOrReception, p => p.RequireRole(RoleNames.Admin, RoleNames.Reception));
        options.AddPolicy(Policies.AdminOrFinance, p => p.RequireRole(RoleNames.Admin, RoleNames.Finance));
        options.AddPolicy(Policies.AnyStaff, p => p.RequireRole(RoleNames.Admin, RoleNames.Reception, RoleNames.Finance));
        options.AddPolicy(Policies.AdminOrCustomer, p => p.RequireRole(RoleNames.Admin, RoleNames.Customer));
        options.AddPolicy(Policies.ReceptionOnly, p => p.RequireRole(RoleNames.Reception));
        options.AddPolicy(Policies.ReceptionOrFinance, p => p.RequireRole(RoleNames.Reception, RoleNames.Finance));
        options.AddPolicy(Policies.FinanceOnly, p => p.RequireRole(RoleNames.Finance));
        options.AddPolicy(Policies.CustomerOnly, p => p.RequireRole(RoleNames.Customer));
        options.AddPolicy(Policies.AdminOrReceptionOrCustomer, p => p.RequireRole(RoleNames.Admin, RoleNames.Reception, RoleNames.Customer));
        options.AddPolicy(Policies.AnyRole, p => p.RequireRole(RoleNames.Admin, RoleNames.Reception, RoleNames.Finance, RoleNames.Customer));
        options.AddPolicy(Policies.AnyAuthenticated, p => p.RequireAuthenticatedUser());
    }
}

using ERental.Models;

namespace ERental.Authorization;

/// <summary>
/// 角色-权限授予关系（RBAC 的多对多映射，对应需求规格说明书 4.2.1「角色权限管理」）。
///
/// 为什么不在库里建关联表：
///   系统的权限划分是需求定死的三档（管理员 / 前台 / 财务），运行期没有「自定义角色」「在线改权限」
///   这类功能，也无法通过接口增删授予关系。为 33 条固定授予关系单独落一张连接表，代价是
///   表数 +1、字段 +2、数据词典 +1 张表，而收益（可查询、可审计）一条都用不上；
///   更麻烦的是同一份口径会分散到三处（本文件、Policies 策略、前端 ROUTE_ROLES），
///   任一处漏改就是「库里授了权、接口仍 403」这类只有联调才能发现的错。
///
/// 因此改为**代码层单一事实来源**：
///   · <see cref="Grants"/> 是角色 → 权限编码的唯一映射，权限的增删改只改这一处；
///   · <see cref="Role.Permissions"/> 与 <see cref="Permission.Roles"/> 是一对 [NotMapped] 导航，
///     由 <see cref="Link"/> 在角色与权限同时加载后按编码对接——对象图上仍是标准的多对多，
///     只是这条关系存在于内存而非库表；
///   · <see cref="CodesOf"/> / <see cref="Has"/> 供鉴权与前端菜单直接取权限码，不依赖数据库。
///
/// 与 Policies 的关系：Policies 回答「哪些角色能进哪个接口」，本文件回答「每个角色被授予了哪些权限项」。
/// 前者是运行期硬约束，后者是权限项的归属清单，两者取值必须同源（管理员全量、前台接单还车、财务看账）。
/// </summary>
public static class RolePermissionMap
{
    /// <summary>
    /// 角色 → 权限编码。键与 <see cref="RoleNames"/>、`role.role_name`、前端 ROUTE_ROLES 完全一致。
    /// 权限编码取自 `permission.permission_code`，种子数据里 20 项，此处按角色归口：
    ///   管理员全量；前台只拿「接单 / 续租 / 还车结算 / 客户档案 / 车辆状态 / 维保上报」；
    ///   财务只拿「看账 / 退款 / 报表导出」。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Grants =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [RoleNames.Admin] = new[]
            {
                "dashboard:view", "order:view", "order:create", "order:cancel", "order:renew",
                "settle:create", "payment:refund", "customer:manage", "vehicle:manage", "vehicle:status",
                "check:manage", "vehicle:scrap", "maintenance:report", "maintenance:handle",
                "report:view", "report:export", "user:manage", "config:manage", "log:view", "account:deactivate"
            },
            [RoleNames.Reception] = new[]
            {
                "dashboard:view", "order:view", "order:create", "order:cancel", "order:renew",
                "settle:create", "customer:manage", "vehicle:status", "maintenance:report"
            },
            [RoleNames.Finance] = new[]
            {
                "dashboard:view", "payment:refund", "report:view", "report:export"
            },
        };

    /// <summary>取某角色被授予的权限编码；角色名不认识（含顾客）时返回空数组，不抛异常。</summary>
    public static IReadOnlyList<string> CodesOf(string? roleName) =>
        roleName is not null && Grants.TryGetValue(roleName, out var codes) ? codes : [];

    /// <summary>判断角色是否被授予某个权限编码（大小写不敏感）。</summary>
    public static bool Has(string? roleName, string permissionCode) =>
        CodesOf(roleName).Contains(permissionCode, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 按权限编码把角色与权限对接起来，填充两端的 [NotMapped] 导航集合。
    /// 调用时机：角色与权限已经同时从库里取出来之后（登录、刷新令牌）。
    /// 重复调用是幂等的：先清空再重建，不会出现同一权限被挂两次。
    /// </summary>
    public static void Link(Role role, IEnumerable<Permission> permissions)
    {
        var byCode = permissions
            .Where(p => !string.IsNullOrWhiteSpace(p.PermissionCode))
            .GroupBy(p => p.PermissionCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var granted = new List<Permission>();
        foreach (var code in CodesOf(role.RoleName))
        {
            if (!byCode.TryGetValue(code, out var permission)) continue;   // 字典里没有的编码直接跳过，不让脏编码拖垮登录
            granted.Add(permission);
        }
        // 集合只在实体内部可写，外部不能再 Clear/Add 篡改授权清单
        role.ReplacePermissions(granted);
        foreach (var permission in granted) permission.LinkRole(role);
    }
}

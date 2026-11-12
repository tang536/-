namespace ERental.DTOs;

public class StaffLoginDto
{
    public string LoginAccount { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string RoleType { get; init; } = string.Empty; // admin / reception / finance
}

/// <summary>顾客登录：**仅支持注册用户名 + 密码**（已移除身份证号登录）</summary>
public class CustomerLoginDto
{
    public string Account { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}

/// <summary>令牌续期：只凭续期令牌本身，不需要再传账号密码</summary>
public class RefreshTokenDto
{
    public string RefreshToken { get; init; } = string.Empty;
}

/// <summary>顾客注册：仅需用户名与密码</summary>
public class CustomerRegisterDto
{
    public string LoginAccount { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}

/// <summary>顾客自助补全租车资料（身份证号 / 手机号 / 姓名）</summary>
public class CustomerProfileDto
{
    public string? FullName { get; init; }
    public string? IdCard { get; init; }
    public string? ContactPhone { get; init; }
}

/// <summary>用户自主注销账号：必须二次输入当前密码确认</summary>
public class DeactivateAccountDto
{
    public string Password { get; init; } = string.Empty;
}

/// <summary>顾客自助修改登录密码：必须先验证原密码，防止改密动作被冒用</summary>
public class ChangePasswordDto
{
    public string OldPassword { get; init; } = string.Empty;
    public string NewPassword { get; init; } = string.Empty;
}

public class LoginResponseDto
{
    public string Token { get; init; } = string.Empty;
    /// <summary>staff / customer</summary>
    public string UserType { get; init; } = string.Empty;
    /// <summary>系统角色：管理员 / 前台 / 财务 / 顾客（用于前端路由与菜单）</summary>
    public string RoleName { get; init; } = string.Empty;
    /// <summary>会员等级：仅顾客有效</summary>
    public string? MemberLevel { get; init; }
    /// <summary>
    /// 该角色被授予的权限编码（如 order:create、report:export）。
    /// 来源是代码里的 RolePermissionMap 授予清单，不是库表——角色与权限的多对多关系在代码层维护，
    /// 库里没有连接表。前端可用它做按钮级 gating，也可只按 RoleName 走菜单，两种都能用。
    /// 顾客不计权限项，返回空数组。
    /// </summary>
    public IReadOnlyList<string> PermissionCodes { get; init; } = [];
    public string UserName { get; init; } = string.Empty;
    public int UserId { get; init; }
    /// <summary>顾客租车资料是否已完善</summary>
    public bool ProfileCompleted { get; init; } = true;
    /// <summary>
    /// 续期令牌：access token 过期后用它换新令牌，避免用户填到一半被踢回登录页。
    /// 无状态实现（不新增会话表），有效性绑定在 JWT 的 sid 上——单点登录被顶掉后此令牌自动失效。
    /// </summary>
    public string RefreshToken { get; init; } = string.Empty;
}

// 注：ApiResponse<T> 统一由 ERental.Models.ApiResponse 提供（原此处有一份完全相同的副本，
// 导致同时 using ERental.Models 与 ERental.DTOs 的文件报 CS0104「不明确的引用」，
// 需要在每个文件里写 using 别名绕过。副本已删除，新增控制器请直接 using ERental.Models;）。

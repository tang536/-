using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace ERental.Models;

[Table("system_user")]
public class SystemUser
{
    [Key]
    [Column("user_id")]
    public int UserId { get; init; }

    [Required, MaxLength(20)]
    [Column("login_account")]
    public string LoginAccount { get; private set; } = string.Empty;

    /// <summary>
    /// 登录密码（BCrypt 哈希）。任何接口都不允许回传给前端；
    /// 员工列表走 UserDto（本身不含密码字段），此处的 [JsonIgnore] 是双保险，
    /// 防止将来有人误把 SystemUser 实体直接序列化时外泄。
    /// </summary>
    [JsonIgnore]
    [Required, MaxLength(100)]
    [Column("login_pwd")]
    public string LoginPwd { get; private set; } = string.Empty;

    [Column("role_id")]
    public int RoleId { get; private set; }

    [ForeignKey("RoleId")]
    public Role? Role { get; init; }

    [MaxLength(20)]
    [Column("user_name")]
    public string? UserName { get; private set; }

    [Column("user_status")]
    public int UserStatus { get; private set; } = 1;

    [Column("login_fail_count")]
    public int LoginFailCount { get; private set; }

    [Column("lock_until")]
    public DateTime? LockUntil { get; private set; }

    // ------------------------------------------------------------------ 工厂
    /// <summary>
    /// 新建员工账号。用泛型工厂是为了让 TPH 落到正确的子类：
    /// <c>SystemUser.CreateStaff&lt;AdminUser&gt;(...)</c> 写入的 user_type 是 admin，
    /// <c>ReceptionUser</c> / <c>FinanceUser</c> 同理；直接 new 一个 SystemUser 只会得到 staff。
    /// 密码只接收已算好的 BCrypt 哈希——实体不该知道哈希算法，也不该接触明文。
    /// </summary>
    public static T CreateStaff<T>(string loginAccount, string passwordHash, int roleId, string? userName = null)
        where T : SystemUser, new()
    {
        var user = new T
        {
            LoginAccount = loginAccount.Trim(),
            LoginPwd = passwordHash,
            RoleId = roleId,
            UserName = string.IsNullOrWhiteSpace(userName) ? loginAccount.Trim() : userName.Trim(),
            UserStatus = 1,
            LoginFailCount = 0,
            LockUntil = null,
        };
        return user;
    }

    // -------------------------------------------------------------- 领域方法
    /// <summary>改显示名（空值表示不修改，与前端「只提交改过的项」一致）。</summary>
    public void Rename(string? userName)
    {
        if (!string.IsNullOrWhiteSpace(userName)) UserName = userName.Trim();
    }

    /// <summary>
    /// 重置登录密码。改密同时清空失败计数与锁定时间——
    /// 管理员重置密码的场景往往是「员工把自己锁在门外」，重置完还要等锁定到期等于没解决。
    /// </summary>
    public void SetPasswordHash(string passwordHash)
    {
        LoginPwd = passwordHash;
        LoginFailCount = 0;
        LockUntil = null;
    }

    /// <summary>调整所属角色（权限随角色走）。</summary>
    public void AssignRole(int roleId) => RoleId = roleId;

    /// <summary>启用/停用账号（1=启用，0=停用）。停用后该账号的在线会话由服务层吊销。</summary>
    public void ChangeStatus(int status) => UserStatus = status;

    /// <summary>停用账号（注销员工时用）</summary>
    public void Deactivate() => UserStatus = 0;

    /// <summary>登录失败一次：累计失败次数 +1，达到阈值时的锁定时间由调用方写入 <see cref="Lock"/>。</summary>
    public void RecordLoginFailure() => LoginFailCount++;

    /// <summary>登录成功：清零失败计数并解除锁定。</summary>
    public void RecordLoginSuccess()
    {
        LoginFailCount = 0;
        LockUntil = null;
    }

    /// <summary>锁定账号到指定时刻（按来源 IP / 账号的失败策略由服务层判定）。</summary>
    public void Lock(DateTime until) => LockUntil = until;
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using ERental.Services;

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

    ///<summary>登录密码（BCrypt 哈希）</summary>
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

    ///<summary>TPH 判别值：与 AppDbContext 中 HasValue 注册的 user_type 一致</summary>
    [NotMapped]
    public virtual string UserType => "staff";

    //该员工收到的站内消息
    private readonly List<AppMessage> _messages = [];
    [JsonIgnore]
    public IReadOnlyCollection<AppMessage> Messages => _messages;
    ///<summary>新建员工账号</summary>
    public static T CreateStaff<T>(string loginAccount, string passwordHash, int roleId, string? userName = null)
        where T : SystemUser, new()
    {
        if (string.IsNullOrWhiteSpace(loginAccount)) throw new BusinessException("登录账号不能为空");
        if (string.IsNullOrWhiteSpace(passwordHash)) throw new BusinessException("密码哈希不能为空");
        if (roleId <= 0) throw new BusinessException("员工必须关联有效角色");

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

    ///<summary>改显示名（空值表示不修改，与前端「只提交改过的项」一致）</summary>
    public void Rename(string? userName)
    {
        if (!string.IsNullOrWhiteSpace(userName)) UserName = userName.Trim();
    }

    ///<summary>重置登录密码</summary>
    public void SetPasswordHash(string passwordHash)
    {
        LoginPwd = passwordHash;
        LoginFailCount = 0;
        LockUntil = null;
    }

    ///<summary>启用/停用账号（1=启用，0=停用）</summary>
    public void ChangeStatus(int status) => UserStatus = status;

    ///<summary>停用账号（注销员工时用）</summary>
    public void Deactivate() => UserStatus = 0;

    ///<summary>登录失败一次</summary>
    public void RecordLoginFailure() => LoginFailCount++;

    ///<summary>登录成功：清零失败计数并解除锁定</summary>
    public void RecordLoginSuccess()
    {
        LoginFailCount = 0;
        LockUntil = null;
    }
}

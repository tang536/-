using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

/// <summary>管理员，继承SystemUser，拥有全部权限</summary>
public class AdminUser : SystemUser
{
    /// <summary>TPH 判别值：system_user.user_type = admin</summary>
    [NotMapped]
    public override string UserType => "admin";
}

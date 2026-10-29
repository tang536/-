using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

/// <summary>前台，继承SystemUser，负责租赁业务</summary>
public class ReceptionUser : SystemUser
{
    /// <summary>TPH 判别值：system_user.user_type = reception</summary>
    [NotMapped]
    public override string UserType => "reception";
}

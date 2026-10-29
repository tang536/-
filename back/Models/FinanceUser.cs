using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

/// <summary>财务，继承SystemUser，负责财务核对</summary>
public class FinanceUser : SystemUser
{
    /// <summary>TPH 判别值：system_user.user_type = finance</summary>
    [NotMapped]
    public override string UserType => "finance";
}

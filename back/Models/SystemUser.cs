using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

[Table("system_user")]
public class SystemUser
{
    [Key]
    [Column("user_id")]
    public int UserId { get; init; }

    [Required, MaxLength(20)]
    [Column("login_account")]
    public string LoginAccount { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    [Column("login_pwd")]
    public string LoginPwd { get; set; } = string.Empty;

    [Column("role_id")]
    public int RoleId { get; set; }

    [ForeignKey("RoleId")]
    public Role? Role { get; init; }

    [MaxLength(20)]
    [Column("user_name")]
    public string? UserName { get; set; }

    [Column("user_status")]
    public int UserStatus { get; set; } = 1;

    [Column("login_fail_count")]
    public int LoginFailCount { get; set; }

    [Column("lock_until")]
    public DateTime? LockUntil { get; set; }
}

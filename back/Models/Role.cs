using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace ERental.Models;

[Table("role")]
public class Role
{
    [Key]
    [Column("role_id")]
    public int RoleId { get; init; }

    [Required, MaxLength(20)]
    [Column("role_name")]
    public string RoleName { get; init; } = string.Empty;

    [MaxLength(100)]
    [Column("role_desc")]
    public string? RoleDesc { get; init; }

    // 角色下的账号只给 EF 用，不进 JSON：角色接口若带上它，
    // 一是把该角色所有员工档案（含 login_pwd 列）整片带出去，二是 SystemUser.Role 指回来会成环。
    [JsonIgnore] public ICollection<SystemUser> SystemUsers { get; init; } = new List<SystemUser>();
}

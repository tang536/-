using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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

    public ICollection<SystemUser> SystemUsers { get; init; } = new List<SystemUser>();
}

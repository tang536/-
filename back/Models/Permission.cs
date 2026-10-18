using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

[Table("permission")]
public class Permission
{
    [Key]
    [Column("permission_id")]
    public int PermissionId { get; init; }

    [Required, MaxLength(50)]
    [Column("permission_name")]
    public string PermissionName { get; init; } = string.Empty;

    [MaxLength(100)]
    [Column("permission_code")]
    public string? PermissionCode { get; init; }

    [MaxLength(200)]
    [Column("permission_desc")]
    public string? PermissionDesc { get; init; }

    [MaxLength(50)]
    [Column("module_name")]
    public string? ModuleName { get; init; }
}

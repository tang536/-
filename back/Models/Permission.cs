using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using ERental.Services;
namespace ERental.Models;

[Table("permission")]
public class Permission
{
    [Key]
    [Column("permission_id")]
    public int PermissionId { get; init; }

    [Required, MaxLength(50)]
    [Column("permission_name")]
    public string PermissionName { get; private set; } = string.Empty;

    [MaxLength(100)]
    [Column("permission_code")]
    public string? PermissionCode { get; private set; }

    [MaxLength(200)]
    [Column("permission_desc")]
    public string? PermissionDesc { get; private set; }

    [MaxLength(50)]
    [Column("module_name")]
    public string? ModuleName { get; private set; }

    ///<summary>反向导航：该权限被哪些角色持有</summary>
    private readonly List<Role> _roles = [];
    [NotMapped]
    [JsonIgnore]
    public IReadOnlyCollection<Role> Roles => _roles;
    ///<summary>维护反向导航：同一角色在同一权限下只挂一次</summary>
    internal void LinkRole(Role role)
    {
        if (!_roles.Contains(role)) _roles.Add(role);
    }

    ///<summary>新增权限项：权限码是授权判定的唯一键，必须非空且去空格</summary>
    public static Permission Create(string permissionName, string permissionCode, string moduleName, string? permissionDesc)
    {
        if (string.IsNullOrWhiteSpace(permissionName)) throw new BusinessException("权限名称不能为空");
        if (string.IsNullOrWhiteSpace(permissionCode)) throw new BusinessException("权限编码不能为空");
        if (string.IsNullOrWhiteSpace(moduleName)) throw new BusinessException("权限所属模块不能为空");
        return new Permission
        {
            PermissionName = permissionName.Trim(),
            PermissionCode = permissionCode.Trim(),
            ModuleName = moduleName.Trim(),
            PermissionDesc = string.IsNullOrWhiteSpace(permissionDesc) ? null : permissionDesc.Trim(),
        };
    }
}

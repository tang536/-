using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using ERental.Services;

namespace ERental.Models;

[Table("role")]
public class Role
{
    [Key]
    [Column("role_id")]
    public int RoleId { get; init; }

    [Required, MaxLength(20)]
    [Column("role_name")]
    public string RoleName { get; private set; } = string.Empty;

    [MaxLength(100)]
    [Column("role_desc")]
    public string? RoleDesc { get; private set; }

    //角色下的账号只给 EF 用
    private readonly List<SystemUser> _systemUsers = [];
    [JsonIgnore] public IReadOnlyCollection<SystemUser> SystemUsers => _systemUsers;
    ///<summary>角色已授予的权限（多对多）</summary>
    private readonly List<Permission> _permissions = [];
    [NotMapped]
    [JsonIgnore]
    public IReadOnlyCollection<Permission> Permissions => _permissions;
    ///<summary>重建本角色的授权清单</summary>
    internal void ReplacePermissions(IEnumerable<Permission> permissions)
    {
        _permissions.Clear();
        foreach (var p in permissions.Where(p => !_permissions.Contains(p)))
            _permissions.Add(p);
    }

    ///<summary>新增角色</summary>
    public static Role Create(string roleName, string? roleDesc)
    {
        if (string.IsNullOrWhiteSpace(roleName)) throw new BusinessException("角色名称不能为空");
        return new Role
        {
            RoleName = roleName.Trim(),
            RoleDesc = string.IsNullOrWhiteSpace(roleDesc) ? null : roleDesc.Trim(),
        };
    }
}

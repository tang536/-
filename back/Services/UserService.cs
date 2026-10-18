using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class UserService(AppDbContext db, SessionRegistry sessions)
{
    private readonly AppDbContext _db = db;
    private readonly SessionRegistry _sessions = sessions;

    /// <summary>密码长度上限：与登录列 VARCHAR(100) 保持足够余量。</summary>
    private const int MaxPasswordLength = 50;

    public async Task<List<UserDto>> GetAllStaff()
    {
        return await _db.SystemUsers.Include(u => u.Role)
            .Where(u => u.Role!.RoleName != "管理员")
            .Select(u => new UserDto
            {
                UserId = u.UserId,
                LoginAccount = u.LoginAccount,
                UserName = u.UserName ?? "",
                RoleName = u.Role!.RoleName,
                UserStatus = u.UserStatus,
                LoginFailCount = u.LoginFailCount
            }).ToListAsync();
    }


    public async Task<UserDto> CreateStaff(CreateUserDto dto)
    {
        if (await _db.SystemUsers.AnyAsync(u => u.LoginAccount == dto.LoginAccount))
            throw new Exception("账号已存在");
        if (!Validators.IsStrongPassword(dto.Password))
            throw new Exception(Validators.PasswordRuleHint);
        if (dto.Password.Length > MaxPasswordLength)
            throw new Exception($"密码长度不能超过 {MaxPasswordLength} 位");
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.RoleName == (dto.RoleType == "reception" ? "前台" : "财务"))
            ?? throw new Exception("角色不存在");

        SystemUser user = dto.RoleType == "reception" ? new ReceptionUser() : new FinanceUser();
        user.LoginAccount = dto.LoginAccount;
        user.LoginPwd = BCrypt.Net.BCrypt.HashPassword(dto.Password);
        user.UserName = dto.UserName;
        user.RoleId = role.RoleId;
        user.UserStatus = 1;

        _db.SystemUsers.Add(user);
        await _db.SaveChangesAsync();
        return new UserDto
        {
            UserId = user.UserId,
            LoginAccount = user.LoginAccount,
            UserName = user.UserName ?? "",
            RoleName = role.RoleName,
            UserStatus = 1
        };
    }

    public async Task UpdateStaff(int id, UpdateUserDto dto)
    {
        var user = await _db.SystemUsers.FindAsync(id) ?? throw new Exception("用户不存在");
        if (!string.IsNullOrEmpty(dto.UserName)) user.UserName = dto.UserName;
        if (!string.IsNullOrEmpty(dto.Password))
        {
            if (!Validators.IsStrongPassword(dto.Password)) throw new Exception(Validators.PasswordRuleHint);
            if (dto.Password.Length > MaxPasswordLength) throw new Exception($"密码长度不能超过 {MaxPasswordLength} 位");
            user.LoginPwd = BCrypt.Net.BCrypt.HashPassword(dto.Password);
            user.LoginFailCount = 0;                                   // 重置密码同时解除失败计数
            user.LockUntil = null;
        }
        if (dto.UserStatus.HasValue) user.UserStatus = dto.UserStatus.Value;
        await _db.SaveChangesAsync();

        // 管理员改了密码或停用账号 → 立刻清掉该账号的在线会话：
        // 否则「被停用的人」凭旧 token 还能继续用到过期，改密也形同没改。
        if (!string.IsNullOrEmpty(dto.Password))
            _sessions.RevokeAll("staff", id, "密码已被管理员重置，请用新密码重新登录");
        if (dto.UserStatus == 0)
            _sessions.RevokeAll("staff", id, "账号已被管理员停用");
    }

    public async Task DeleteStaff(int id)
    {
        var user = await _db.SystemUsers.FindAsync(id) ?? throw new Exception("用户不存在");
        if (user.LoginAccount == "trq") throw new Exception("默认管理员不可删除");
        _db.SystemUsers.Remove(user);
        await _db.SaveChangesAsync();
        _sessions.RevokeAll("staff", id, "账号已被删除，登录状态已失效");
    }
}

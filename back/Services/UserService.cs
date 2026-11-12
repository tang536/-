using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class UserService(AppDbContext db,SessionRegistry sessions)
{
    private readonly AppDbContext _db=db;
    private readonly SessionRegistry _sessions=sessions;

    ///<summary>密码长度上限：与登录列 VARCHAR(100) 保持足够余量</summary>
    private const int MaxPasswordLength=50;

    public async Task<List<UserDto>> GetAllStaff()
    {
        return await _db.SystemUsers.Include(u=>u.Role).Where(u=>u.Role!.RoleName!="管理员").Select(u=>new UserDto
            {
                UserId=u.UserId,
                LoginAccount=u.LoginAccount,
                UserName=u.UserName??"",
                RoleName=u.Role!.RoleName,
                UserStatus=u.UserStatus,
                LoginFailCount=u.LoginFailCount
            }).ToListAsync();
    }

    public async Task<UserDto> CreateStaff(CreateUserDto dto)
    {
        if(await _db.SystemUsers.AnyAsync(u=>u.LoginAccount==dto.LoginAccount)) throw new BusinessException("账号已存在");
        if(!Validators.IsStrongPassword(dto.Password)) throw new BusinessException(Validators.PasswordRuleHint);
        if(dto.Password.Length>MaxPasswordLength) throw new BusinessException($"密码长度不能超过 {MaxPasswordLength} 位");
        var role=await _db.Roles.FirstOrDefaultAsync(r=>r.RoleName==(dto.RoleType=="reception"?"前台":"财务"))??throw new BusinessException("角色不存在");

        //按角色落 TPH 子类：reception → ReceptionUser，finance
        var hash=BCrypt.Net.BCrypt.HashPassword(dto.Password);
        SystemUser user=dto.RoleType=="reception"?SystemUser.CreateStaff<ReceptionUser>(dto.LoginAccount,hash,role.RoleId,dto.UserName):SystemUser.CreateStaff<FinanceUser>(dto.LoginAccount,hash,role.RoleId,dto.UserName);

        _db.SystemUsers.Add(user);
        await _db.SaveChangesAsync();
        return new UserDto
        {
            UserId=user.UserId,
            LoginAccount=user.LoginAccount,
            UserName=user.UserName??"",
            RoleName=role.RoleName,
            UserStatus=1
        };
    }

    public async Task UpdateStaff(int id,UpdateUserDto dto)
    {
        var user=await _db.SystemUsers.FindAsync(id)??throw new BusinessException("用户不存在");

        //停用（或删除）前的最后一名管理员保护：见 EnsureNotLastActiveAdmin 注释
        if(dto.UserStatus==0&&user.UserStatus==1) await EnsureNotLastActiveAdmin(user,"停用");

        user.Rename(dto.UserName);
        if(!string.IsNullOrEmpty(dto.Password))
        {
            if(!Validators.IsStrongPassword(dto.Password)) throw new BusinessException(Validators.PasswordRuleHint);
            if(dto.Password.Length>MaxPasswordLength) throw new BusinessException($"密码长度不能超过 {MaxPasswordLength} 位");
            //重置密码同时解除失败计数与锁定
            user.SetPasswordHash(BCrypt.Net.BCrypt.HashPassword(dto.Password));
        }
        if(dto.UserStatus.HasValue) user.ChangeStatus(dto.UserStatus.Value);
        await _db.SaveChangesAsync();

        //管理员改了密码或停用账号 → 立刻清掉该账号的在线会话
        if(!string.IsNullOrEmpty(dto.Password)) await _sessions.RevokeAllAsync(user.UserType,id,"密码已被管理员重置，请用新密码重新登录");
        if(dto.UserStatus==0) await _sessions.RevokeAllAsync(user.UserType,id,"账号已被管理员停用");
    }

    public async Task DeleteStaff(int id)
    {
        var user=await _db.SystemUsers.FindAsync(id)??throw new BusinessException("用户不存在");
        if(user.LoginAccount=="trq") throw new BusinessException("默认管理员不可删除");
        await EnsureNotLastActiveAdmin(user,"删除");
        //日志只增不删：账号可以删，历史操作不能跟着消失
        await _db.SystemLogs.Where(l=>l.UserId==id)
            .ExecuteUpdateAsync(s=>s.SetProperty(l=>l.UserId,(int?)null));
        _db.SystemUsers.Remove(user);
        await _db.SaveChangesAsync();
        await _sessions.RevokeAllAsync(user.UserType,id,"账号已被删除，登录状态已失效");
    }

    ///<summary>最后一名管理员保护</summary>
    private async Task EnsureNotLastActiveAdmin(SystemUser user,string action)
    {
        var roleName=await _db.Roles.Where(r=>r.RoleId==user.RoleId).Select(r=>r.RoleName).FirstOrDefaultAsync();
        if(roleName!="管理员") return;

        //已被停用的管理员不占名额
        if(user.UserStatus!=1) return;

        var activeAdmins=await _db.SystemUsers.CountAsync(u=>u.Role!.RoleName=="管理员"&&u.UserStatus==1);
        if(activeAdmins<=1) throw new BusinessException($"系统中至少保留一名启用状态的管理员，无法{action}「{user.LoginAccount}」");
    }
}

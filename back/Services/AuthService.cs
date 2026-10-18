using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace ERental.Services;

public class AuthService(AppDbContext db, IConfiguration config, SessionRegistry sessions)
{
    private readonly AppDbContext _db = db;
    private readonly IConfiguration _config = config;
    private readonly SessionRegistry _sessions = sessions;

    // 角色名常量：与 DTOs/AuthDtos 的 RoleType（admin/reception/finance）、
    // 数据库 Roles.RoleName 种子值、[Authorize(Roles = "...")] 三处保持同源。
    private const string RoleAdmin = "管理员";
    private const string RoleReception = "前台";
    private const string RoleFinance = "财务";
    public const string RoleCustomer = "顾客";

    /// <summary>访问令牌类型：有效期短，用于每次业务请求（仅本服务内部使用，外部只判断"是不是续期令牌"）</summary>
    private const string TokenTypeAccess = "access";

    /// <summary>续期令牌类型：有效期长，只用于换新令牌，不能直接访问业务接口</summary>
    public const string TokenTypeRefresh = "refresh";

    /// <summary>JWT 中区分令牌用途的声明名（与 Program.cs 的 JwtBearer 校验事件同源）</summary>
    public const string TokenTypeClaim = "token_type";

    public async Task<LoginResponseDto> StaffLogin(StaffLoginDto dto)
    {
        // MySQL 列排序规则为 _ci（不区分大小写），SQL 比较会让 TRQ 命中 trq；
        // 登录账号必须精确匹配——先按列取候选（可走索引），再在内存按 ordinal 大小写敏感过滤
        var candidates = await _db.SystemUsers.Include(u => u.Role)
            .Where(u => u.LoginAccount == dto.LoginAccount)
            .ToListAsync();
        var user = candidates.FirstOrDefault(u =>
            string.Equals(u.LoginAccount, dto.LoginAccount, StringComparison.Ordinal));
        if (user == null)
        {
            // 需求 4.2.1：登录、异常行为全流程留痕（日志不可删除、可追溯）
            await WriteLog(null, $"登录失败：账号不存在（{dto.LoginAccount}）", "用户登录", "失败");
            throw new Exception("账号不存在");
        }

        // 角色分端隔离：登录页选的是「管理员/前台/财务」哪个页签，就必须是该角色的账号。
        //
        // 为什么统一报「账号不存在」而不是「角色不符」：
        // 后者等于告诉对方「这个账号确实存在，只是角色不对」，属于账号枚举漏洞，
        // 会被用来逐个试探系统里有哪些账号。统一文案可避免泄露账号是否存在。
        var actualRole = user.Role?.RoleName ?? RoleReception;
        var expectedRole = MapRoleType(dto.RoleType);
        if (expectedRole != null && actualRole != expectedRole)
        {
            await WriteLog(user.UserId,
                $"登录失败：角色不匹配（账号 {user.LoginAccount} 实际为「{actualRole}」，却在「{expectedRole}」入口登录）",
                "用户登录", "失败");
            throw new Exception("账号不存在");
        }

        if (user.UserStatus == 0)
        {
            await WriteLog(user.UserId, $"登录失败：账号已被禁用（{user.LoginAccount}）", "用户登录", "失败");
            throw new Exception("账号已被禁用");
        }
        if (user.LockUntil.HasValue && user.LockUntil > DateTime.Now)
            throw new Exception($"账号已锁定，请于 {user.LockUntil:HH:mm} 后重试");

        if (!BCrypt.Net.BCrypt.Verify(dto.Password, user.LoginPwd))
        {
            user.LoginFailCount++;
            if (user.LoginFailCount >= 5)
            {
                user.LockUntil = DateTime.Now.AddMinutes(10);
                user.LoginFailCount = 0;
                await _db.SaveChangesAsync();
                await WriteLog(user.UserId, $"登录失败次数过多，账号锁定10分钟（{user.LoginAccount}）", "用户登录", "失败");
                throw new Exception("密码错误次数过多，账号已锁定10分钟");
            }
            await _db.SaveChangesAsync();
            await WriteLog(user.UserId, $"登录失败：密码错误，剩余 {5 - user.LoginFailCount} 次机会（{user.LoginAccount}）", "用户登录", "失败");
            throw new Exception($"密码错误，还剩 {5 - user.LoginFailCount} 次机会");
        }

        user.LoginFailCount = 0;
        user.LockUntil = null;
        await _db.SaveChangesAsync();
        await WriteLog(user.UserId, $"登录成功（{user.LoginAccount}）", "用户登录", "成功");

        // 关键：角色一律以数据库中的真实角色为准，前端不再根据"登录时点的哪个页签"决定跳转
        var roleName = user.Role?.RoleName ?? RoleReception;
        // 单点登录：先登记会话（顶掉该账号此前的登录），再把会话标识签进 token
        var sid = await _sessions.IssueAsync("staff", user.UserId);
        var token = GenerateJwt(user.UserId, user.LoginAccount, "staff", roleName, sid);
        return new LoginResponseDto
        {
            Token = token,
            // 续期令牌：与 access token 同一个 sid，单点登录被顶掉后它一并失效
            RefreshToken = GenerateJwt(user.UserId, user.LoginAccount, "staff", roleName, sid,
                TokenTypeRefresh, RefreshLifetime()),
            UserType = "staff",
            RoleName = roleName,
            UserName = user.UserName ?? user.LoginAccount,
            UserId = user.UserId,
            ProfileCompleted = true
        };
    }

    /// <summary>
    /// 登录页页签（admin/reception/finance）→ 系统角色名 的映射。
    /// 页签为空时不校验（兼容旧前端 / 其它调用方）；填了就必须匹配。
    /// </summary>
    private static string? MapRoleType(string? roleType) => roleType?.Trim().ToLowerInvariant() switch
    {
        "admin" => RoleAdmin,
        "reception" => RoleReception,
        "finance" => RoleFinance,
        _ => null
    };

    /// <summary>
    /// 顾客登录：**只认「用户名 + 密码」**。
    /// 已按需求移除「身份证号登录」入口与匹配逻辑——身份证号属敏感个人信息，
    /// 不应作为可枚举的登录凭据（且租车环节才采集，注册阶段根本不存在）。
    /// </summary>
    public async Task<LoginResponseDto> CustomerLogin(CustomerLoginDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Account)) throw new Exception("请输入用户名");

        var account = dto.Account.Trim();
        // 同员工登录：_ci 排序规则下 SQL 比较不区分大小写，顾客登录也必须精确匹配
        var cCandidates = await _db.Customers
            .Where(c => c.LoginAccount == account)
            .ToListAsync();
        var customer = cCandidates.FirstOrDefault(c =>
            string.Equals(c.LoginAccount, account, StringComparison.Ordinal));
        if (customer == null)
        {
            // 员工账号不在 customers 表，天然落到这里 → 员工在顾客端登录会提示「用户名未注册」，
            // 与「顾客账号在员工端提示账号不存在」形成双向隔离。
            await WriteLog(null, $"顾客登录失败：用户名未注册（{account}）", "用户登录", "失败");
            throw new Exception("用户名未注册，请先注册或核对用户名");
        }
        if (customer.AccountStatus == "已注销")
            throw new Exception("该账号已注销，如需继续租车请重新注册或联系门店");
        if (string.IsNullOrEmpty(customer.LoginPassword) || !BCrypt.Net.BCrypt.Verify(dto.Password, customer.LoginPassword))
        {
            await WriteLog(null, $"顾客登录失败：密码错误（{account}）", "用户登录", "失败");
            throw new Exception("密码错误");
        }
        await WriteLog(null, $"顾客登录成功（{account}）", "用户登录", "成功");

        // 单点登录：顾客端同理——同一账号在第二台设备登录会把先登录的那台顶下线
        var sid = await _sessions.IssueAsync("customer", customer.CustomerId);
        var token = GenerateJwt(customer.CustomerId, customer.LoginAccount ?? account, "customer", RoleCustomer, sid);
        return new LoginResponseDto
        {
            Token = token,
            RefreshToken = GenerateJwt(customer.CustomerId, customer.LoginAccount ?? account, "customer",
                RoleCustomer, sid, TokenTypeRefresh, RefreshLifetime()),
            UserType = "customer",
            // 关键修复：顾客的角色名固定为「顾客」，不能再用会员等级充当角色，
            // 否则前端菜单表匹配不到，会回退成管理员菜单。
            RoleName = RoleCustomer,
            MemberLevel = customer.MemberLevel,
            UserName = customer.FullName,
            UserId = customer.CustomerId,
            ProfileCompleted = customer.ProfileCompleted
        };
    }

    /// <summary>顾客注册：仅需用户名 + 密码</summary>
    public async Task<Customer> CustomerRegister(CustomerRegisterDto dto)
    {
        var account = dto.LoginAccount.Trim();
        if (!Validators.IsLoginAccount(account))
            throw new Exception("用户名需为 3-20 位字母、数字、下划线或中文");
        if (!Validators.IsStrongPassword(dto.Password))
            throw new Exception(Validators.PasswordRuleHint);

        if (await _db.Customers.AnyAsync(c => c.LoginAccount == account))
            throw new Exception("该用户名已被注册");

        var customer = new Customer
        {
            LoginAccount = account,
            // 姓名暂用用户名占位，租车时可由前台/顾客本人更新为真实姓名
            FullName = account.Length > 20 ? account[..20] : account,
            LoginPassword = BCrypt.Net.BCrypt.HashPassword(dto.Password),
            MemberLevel = "普通会员"
            // 3NF：折扣不落档案，注册即普通会员（权益表无该等级时按不打折处理）
        };
        // 身份证号与手机号在租车环节采集，注册阶段不强制
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        return customer;
    }

    /// <summary>顾客自助补全租车资料（身份证号、手机号、真实姓名）</summary>
    public async Task<Customer> UpdateCustomerProfile(int customerId, CustomerProfileDto dto)
    {
        var customer = await _db.Customers.FindAsync(customerId) ?? throw new Exception("客户不存在");
        if (!string.IsNullOrWhiteSpace(dto.IdCard))
        {
            var idCard = dto.IdCard.Trim();
            if (!Validators.IsIdCard(idCard)) throw new Exception("身份证号格式不正确");
            if (await _db.Customers.AnyAsync(c => c.IdCard == idCard && c.CustomerId != customerId))
                throw new Exception("该身份证号已被其他账号使用");
            customer.IdCard = idCard;
        }
        if (!string.IsNullOrWhiteSpace(dto.ContactPhone))
        {
            var phone = dto.ContactPhone.Trim();
            if (!Validators.IsPhone(phone)) throw new Exception("手机号格式不正确");
            customer.ContactPhone = phone;
        }
        if (!string.IsNullOrWhiteSpace(dto.FullName)) customer.FullName = dto.FullName.Trim();
        customer.Version++;  // 顾客补资料同样参与乐观并发（与员工编辑互相感知）
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new Exception("您的资料刚被门店工作人员更新，请刷新后重试");
        }
        customer.MemberDiscount = await MemberDiscountLookup.OfLevelAsync(_db, customer.MemberLevel);
        return customer;
    }

    /// <summary>
    /// 顾客自助修改登录密码（个人中心入口）。
    /// 三道闸：原密码必须正确（防冒用）→ 新密码过复杂度校验（与注册同规则）→
    /// 新旧不得相同（避免「改了个寂寞」，也让原密码泄露后的一次修改真正生效）。
    /// 成功后写操作日志，满足需求 7.1「账号安全操作全流程留痕」。
    /// </summary>
    public async Task ChangeCustomerPassword(int customerId, ChangePasswordDto dto)
    {
        var oldPwd = dto.OldPassword;
        var newPwd = dto.NewPassword;
        if (string.IsNullOrEmpty(oldPwd)) throw new Exception("请输入原密码");
        if (string.IsNullOrEmpty(newPwd)) throw new Exception("请输入新密码");

        var customer = await _db.Customers.FindAsync(customerId) ?? throw new Exception("账号不存在");
        if (customer.AccountStatus == "已注销") throw new Exception("该账号已注销");
        if (string.IsNullOrEmpty(customer.LoginPassword) || !BCrypt.Net.BCrypt.Verify(oldPwd, customer.LoginPassword))
        {
            await WriteLog(null, $"顾客修改密码失败：原密码错误（{customer.LoginAccount}）", "用户登录", "失败");
            throw new Exception("原密码不正确");
        }
        if (!Validators.IsStrongPassword(newPwd))
            throw new Exception(Validators.PasswordRuleHint);
        if (BCrypt.Net.BCrypt.Verify(newPwd, customer.LoginPassword))
            throw new Exception("新密码不能与原密码相同");

        customer.LoginPassword = BCrypt.Net.BCrypt.HashPassword(newPwd);
        customer.Version++;  // 密码属于账号档案写操作，与资料编辑/注销互斥（乐观并发）
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new Exception("该账号档案刚被工作人员操作，请刷新后重试");
        }
        await WriteLog(null, $"顾客修改登录密码成功（{customer.LoginAccount}）", "用户登录", "成功");
        // 改密后强制下线：凭据已变，改密前签发的 token 不应继续可用
        await _sessions.RevokeAllAsync("customer", customerId, "密码已修改，请用新密码重新登录");
    }

    /// <summary>
    /// 用户自主注销账号（本人操作，需二次输入密码确认）。
    /// **仅对「顾客」与「管理员」开放**（需求约束）：
    /// - 顾客：入口只在「个人中心」；账号置「已注销」并释放用户名 / 清除凭据；
    ///   档案与历史订单保留，满足需求 7.1「数据不可删除、全流程可追溯」。
    /// - 管理员：入口在「账号管理」页；账号置为停用（user_status = 0），复用既有登录校验拦截。
    /// - 前台 / 财务：不提供自助注销（避免门店业务岗位被随手停用），请由管理员处理。
    /// 前置条件：存在未完结订单的顾客、系统中最后一名管理员，均不允许注销。
    /// </summary>
    public async Task Deactivate(int userId, string userType, string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new Exception("请输入当前账号密码以确认注销");

        if (userType == "customer")
        {
            var c = await _db.Customers.FindAsync(userId) ?? throw new Exception("账号不存在");
            if (c.AccountStatus == "已注销") throw new Exception("该账号已注销");
            if (string.IsNullOrEmpty(c.LoginPassword) || !BCrypt.Net.BCrypt.Verify(password, c.LoginPassword))
                throw new Exception("密码错误，无法注销");

            var unfinished = await _db.RentalOrders
                .Where(o => o.CustomerId == userId
                            && o.OrderStatus != "已完成"
                            && o.OrderStatus != "已完结"
                            && o.OrderStatus != "已取消")
                .Select(o => o.OrderNo)
                .ToListAsync();
            if (unfinished.Count > 0)
                throw new Exception($"您还有 {unfinished.Count} 笔未完结订单（{string.Join("、", unfinished.Take(3))}），请先完成归还结算再注销");

            var oldAccount = c.LoginAccount ?? c.FullName;
            c.AccountStatus = "已注销";
            c.LoginAccount = null;   // 释放用户名，便于他人（或本人）重新注册
            c.LoginPassword = null;  // 清除登录凭据
            // 同步释放身份信息：身份证 / 手机号是全局唯一性校验的字段
            //（UpdateCustomerProfile 校验"已被其他账号使用"），不清空会导致注销后
            // 这些信息仍被占用、无法重新实名。姓名改为匿名占位（列非空），
            // 档案与历史订单仍可通过"已注销用户#ID"追溯到同一条记录，满足需求 7.1。
            c.IdCard = null;
            c.ContactPhone = null;
            c.FullName = $"已注销用户#{c.CustomerId}";
            c.Version++;  // 注销也是一次档案写操作，与编辑/等级调整互斥（乐观并发）
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new Exception("该账号档案刚被工作人员操作，请刷新后重试");
            }
            await WriteLog(null, $"顾客自助注销账号：{oldAccount}（身份信息已匿名化释放）", "系统管理", "成功");
            // 账号已注销：立刻把在线会话清掉，避免注销后旧 token 还能继续调接口
            await _sessions.RevokeAllAsync("customer", userId, "账号已注销，登录状态已失效");
            return;
        }

        var user = await _db.SystemUsers.Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.UserId == userId) ?? throw new Exception("账号不存在");
        if (user.Role?.RoleName != RoleAdmin)
            throw new Exception("自助注销仅对「管理员」与「顾客」开放，前台/财务账号请联系管理员处理");
        if (user.UserStatus == 0) throw new Exception("账号已处于停用状态");
        if (!BCrypt.Net.BCrypt.Verify(password, user.LoginPwd))
            throw new Exception("密码错误，无法注销");

        var adminCount = await _db.SystemUsers
            .CountAsync(u => u.UserStatus != 0 && u.Role!.RoleName == RoleAdmin);
        if (adminCount <= 1)
            throw new Exception("当前账号是系统中最后一名管理员，注销后将无人可管理系统，请先新增其他管理员");

        user.UserStatus = 0;
        await _db.SaveChangesAsync();
        await WriteLog(user.UserId, $"管理员自助注销账号：{user.LoginAccount}", "系统管理", "成功");
        // 账号已停用：清掉在线会话（否则停用了却还能凭旧 token 用到过期）
        await _sessions.RevokeAllAsync("staff", user.UserId, "账号已停用，登录状态已失效");
    }

    /// <summary>
    /// 员工关键操作的二次密码校验（用况4 扩展用况3：管理员手动更改车辆状态，
    /// 使其变成其他状态需要管理员密码）。
    ///
    /// 校验对象是**当前登录用户本人**的密码，而不是"随便一个管理员的密码"：
    /// 目的就是确认坐在电脑前的人就是已登录的管理员，避免他人趁管理员离开工位
    /// 顺手把在租/维保车辆改成空闲。校验按 BCrypt 比对，明文不落库、不写日志。
    /// 失败写操作日志（需求 7.1 账号安全操作全流程留痕）。
    /// </summary>
    public async Task VerifyStaffPassword(int userId, string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new Exception("请输入当前管理员的登录密码以确认本次操作");
        var user = await _db.SystemUsers.FirstOrDefaultAsync(u => u.UserId == userId)
            ?? throw new Exception("账号不存在");
        if (string.IsNullOrEmpty(user.LoginPwd) || !BCrypt.Net.BCrypt.Verify(password, user.LoginPwd))
        {
            await WriteLog(userId, $"关键操作二次校验失败：密码错误（{user.LoginAccount}）", "车辆管理", "失败");
            throw new Exception("密码不正确，无法执行该操作");
        }
    }

    /// <summary>
    /// 退出登录：撤销当前这一个会话（单点登录登记表里的那条）。
    /// 只撤销 token 自带的 sid，不会误伤该账号在别处的新会话。
    /// </summary>
    public async Task Logout(string userType, int userId, string? sessionToken) =>
        await _sessions.RevokeAsync(userType, userId, sessionToken);

    private async Task WriteLog(int? userId, string content, string module, string result)
    {
        _db.SystemLogs.Add(new SystemLog
        {
            UserId = userId,
            OperateContent = content,
            OperateModule = module,
            OperateResult = result,
            OperateTime = DateTime.Now
        });
        await _db.SaveChangesAsync();
    }

    private string GenerateJwt(int userId, string account, string userType, string role, string sessionToken,
        string tokenType = TokenTypeAccess, TimeSpan? lifetime = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, account),
            new Claim("user_type", userType),
            new Claim(ClaimTypes.Role, role),
            // sid：本次登录的会话标识。同一账号再次登录会签出新的 sid 并顶掉旧的，
            // 旧 token 随即被 SingleSessionMiddleware 判为失效（同一账号不能同时登录）。
            new Claim(Middleware.SingleSessionMiddleware.SessionClaim, sessionToken),
            // token_type：区分「访问令牌」与「续期令牌」。
            // ⚠️ 必须区分——JWT 签名与过期校验对两者是同一条规则，若不区分，
            // 有效期 7 天的 refresh token 就能直接当 access token 调所有接口。
            // 真正的拦截在 JwtBearer 的 OnTokenValidated 事件里（见 Program.cs）。
            new Claim(TokenTypeClaim, tokenType)
        };
        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.Now.Add(lifetime ?? AccessLifetime()),
            signingCredentials: creds
        );
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>访问令牌有效期（配置 <c>Jwt:ExpireMinutes</c>，默认 120 分钟）</summary>
    private TimeSpan AccessLifetime() =>
        TimeSpan.FromMinutes(double.Parse(_config["Jwt:ExpireMinutes"] ?? "120"));

    /// <summary>续期令牌有效期（配置 <c>Jwt:RefreshExpireDays</c>，默认 7 天）</summary>
    private TimeSpan RefreshLifetime() =>
        TimeSpan.FromDays(double.Parse(_config["Jwt:RefreshExpireDays"] ?? "7"));

    /// <summary>
    /// 用续期令牌换一对新令牌。
    ///
    /// 三个必须校验的点：
    ///  ① 签名与有效期——防止伪造与过期令牌续命；
    ///  ② <c>token_type</c> 必须是 refresh——防止把 access token 当续期凭证反复延长；
    ///  ③ <c>sid</c> 仍是该账号的当前会话——**这是与单点登录的接缝**：
    ///     账号在别处登录或已被强制下线（改密 / 停用 / 注销）时，登记表里的 sid 已变或已墓碑化，
    ///     此时续期必须失败，否则「被顶下线」就形同虚设，旧设备能靠续期令牌一直活下去。
    ///
    /// 续期**不重新 Issue 会话**：沿用原 sid，否则每续一次就把自己顶下线一次。
    /// 角色与账号状态重新查库，不信任旧令牌里的声明——管理员可能刚改过角色或停用了账号。
    /// </summary>
    public async Task<LoginResponseDto> RefreshAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new Exception("登录状态已失效，请重新登录");

        var principal = ValidateRefreshToken(refreshToken);
        var rawId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var account = principal.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty;
        var userType = principal.FindFirst("user_type")?.Value ?? "staff";
        var sid = principal.FindFirst(Middleware.SingleSessionMiddleware.SessionClaim)?.Value;

        if (!int.TryParse(rawId, out var userId) || string.IsNullOrEmpty(sid))
            throw new Exception("登录状态已失效，请重新登录");

        // 与单点登录接缝：会话已被顶掉 / 被强制撤销时，不允许续期
        var (sessionOk, revokedReason) = await _sessions.CheckAsync(userType, userId, sid);
        if (!sessionOk)
            throw new Exception(revokedReason ?? "该账号已在其他设备登录，当前登录已被下线");

        if (userType == "customer")
        {
            var customer = await _db.Customers.FirstOrDefaultAsync(c => c.CustomerId == userId)
                ?? throw new Exception("账号不存在，请重新登录");
            if (customer.AccountStatus == "已注销")
                throw new Exception("该账号已注销，请重新登录");

            return new LoginResponseDto
            {
                // 账号名以库为准；库里为空时回落到续期令牌里的账号声明（登录时必然写入）
                Token = GenerateJwt(userId, customer.LoginAccount ?? account, "customer", RoleCustomer, sid),
                RefreshToken = GenerateJwt(userId, customer.LoginAccount ?? account, "customer", RoleCustomer, sid,
                    TokenTypeRefresh, RefreshLifetime()),
                UserType = "customer",
                RoleName = RoleCustomer,
                // 姓名可能为空（注册只填用户名），为空时回落到令牌里的账号名，避免续期后显示空白
                UserName = string.IsNullOrEmpty(customer.FullName) ? account : customer.FullName,
                UserId = userId,
                MemberLevel = customer.MemberLevel,
                // 复用模型上的计算属性，避免续期后与登录时的口径漂移
                ProfileCompleted = customer.ProfileCompleted
            };
        }

        var user = await _db.SystemUsers.Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.UserId == userId)
            ?? throw new Exception("账号不存在，请重新登录");
        if (user.UserStatus != 1)
            throw new Exception("该账号已被停用，请重新登录");

        var roleName = user.Role?.RoleName ?? RoleReception;
        return new LoginResponseDto
        {
            Token = GenerateJwt(userId, user.LoginAccount, "staff", roleName, sid),
            RefreshToken = GenerateJwt(userId, user.LoginAccount, "staff", roleName, sid,
                TokenTypeRefresh, RefreshLifetime()),
            UserType = "staff",
            RoleName = roleName,
            UserName = user.UserName ?? user.LoginAccount,
            UserId = userId,
            ProfileCompleted = true
        };
    }

    /// <summary>
    /// 校验续期令牌的签名与有效期，并确认它确实是 refresh 类型。
    /// 校验参数与 JwtBearer 保持一致（issuer / audience / 签名算法），否则会出现
    /// 「中间件认可、这里不认可」的诡异不一致。
    /// </summary>
    private ClaimsPrincipal ValidateRefreshToken(string refreshToken)
    {
        var handler = new JwtSecurityTokenHandler();
        try
        {
            var principal = handler.ValidateToken(refreshToken, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _config["Jwt:Issuer"],
                ValidAudience = _config["Jwt:Audience"],
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"]!))
            }, out _);

            // 只接受 refresh 类型：防止把 access token 当续期凭证反复延长有效期
            return principal.FindFirst(TokenTypeClaim)?.Value == TokenTypeRefresh
                ? principal
                : throw new Exception("该令牌不能用于续期，请重新登录");
        }
        catch (Exception ex) when (ex.Message != "该令牌不能用于续期，请重新登录")
        {
            // 签名错误 / 已过期 / 格式非法：统一收敛为同一句人话，不向客户端暴露具体校验环节
            throw new Exception("登录状态已失效，请重新登录");
        }
    }
}

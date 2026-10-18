using System.Security.Claims;
using ERental.DTOs;
using ERental.Models;
using ERental.Services;
using ERental.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ERental.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(AuthService auth) : ControllerBase
{
    private readonly AuthService _auth = auth;

    /// <summary>
    /// 令牌续期：access token 临近过期时用它换一对新令牌，用户无需重新输入密码。
    ///
    /// 为什么允许匿名：此时 access token 可能已经过期，带着过期令牌来换新令牌正是本接口的用途；
    /// 身份由续期令牌自身的签名 + 会话校验保证（详见 <see cref="AuthService.RefreshAsync"/>）。
    /// 返回 401 而非 400：前端据此统一走「清理登录态 + 回登录页」。
    /// </summary>
    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenDto dto)
    {
        try
        {
            return Ok(new ApiResponse<LoginResponseDto> { Data = await _auth.RefreshAsync(dto.RefreshToken) });
        }
        catch (Exception ex)
        {
            return Unauthorized(new ApiResponse<object> { Code = 401, Message = ex.Message, Success = false });
        }
    }

    [AllowAnonymous]   // 登录接口：此时必然没有 JWT，必须显式声明匿名（详见 RequireAuthorizationFilter）
    [HttpPost("staff-login")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> StaffLogin([FromBody] StaffLoginDto dto)
    {
        try { return Ok(new ApiResponse<LoginResponseDto> { Data = await _auth.StaffLogin(dto) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    [AllowAnonymous]
    [HttpPost("customer-login")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> CustomerLogin([FromBody] CustomerLoginDto dto)
    {
        try { return Ok(new ApiResponse<LoginResponseDto> { Data = await _auth.CustomerLogin(dto) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    [AllowAnonymous]
    [HttpPost("customer-register")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> CustomerRegister([FromBody] CustomerRegisterDto dto)
    {
        try
        {
            var c = await _auth.CustomerRegister(dto);
            return Ok(new ApiResponse<object> { Message = "注册成功", Data = new { c.CustomerId, c.LoginAccount, c.FullName } });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    /// <summary>顾客登录后自助补全租车资料（身份证号 / 手机号 / 真实姓名）</summary>
    [HttpPost("customer-profile")]
    [Authorize(Policy = Policies.CustomerOnly)]
    public async Task<IActionResult> UpdateCustomerProfile([FromBody] CustomerProfileDto dto)
    {
        try
        {
            var id = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var c = await _auth.UpdateCustomerProfile(id, dto);
            return Ok(new ApiResponse<object>
            {
                Message = "资料已保存",
                Data = new { c.CustomerId, c.FullName, c.IdCard, c.ContactPhone, c.ProfileCompleted }
            });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    /// <summary>
    /// 顾客自助修改登录密码：需提供原密码验证身份，新密码走复杂度校验（与注册同规则）。
    /// 仅「顾客」开放——员工改密走「账号管理 / 个人改密」（UserService），两套凭据表互不相通。
    /// </summary>
    [HttpPost("customer-change-password")]
    [Authorize(Policy = Policies.CustomerOnly)]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> ChangeCustomerPassword([FromBody] ChangePasswordDto dto)
    {
        try
        {
            var id = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await _auth.ChangeCustomerPassword(id, dto);
            return Ok(new ApiResponse<object> { Message = "密码已修改，下次登录请使用新密码" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    /// <summary>
    /// 退出登录：把当前会话从单点登录登记表中撤销（同一账号不允许同时登录）。
    /// 只撤销本 token 携带的 sid，不影响该账号在别处的新会话；
    /// 前端在清理本地登录态前调用，本身就是"尽力而为"——失败也不该阻止用户登出。
    /// </summary>
    [HttpPost("logout")]
    [Authorize(Policy = Policies.AnyAuthenticated)]
    public async Task<IActionResult> Logout()
    {
        var id = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var userType = User.FindFirst("user_type")?.Value ?? "staff";
        await _auth.Logout(userType, id, User.FindFirst(Middleware.SingleSessionMiddleware.SessionClaim)?.Value);
        return Ok(new ApiResponse<object> { Message = "已退出登录" });
    }

    /// <summary>
    /// 用户自主注销账号：登录用户注销自己（需二次输入密码）。
    /// **仅「顾客」与「管理员」开放**：顾客入口在个人中心，管理员入口在账号管理页；
    /// 前台/财务不在开放范围（后端同步拦截，避免绕过前端直接调接口）。
    /// </summary>
    [HttpPost("deactivate")]
    [Authorize(Policy = Policies.AdminOrCustomer)]
    public async Task<IActionResult> Deactivate([FromBody] DeactivateAccountDto dto)
    {
        try
        {
            var id = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var userType = User.FindFirst("user_type")?.Value ?? "staff";
            await _auth.Deactivate(id, userType, dto.Password);
            return Ok(new ApiResponse<object> { Message = "账号已注销" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false }); }
    }

    /// <summary>
    /// 上传个人头像。按「身份前缀 + 用户 ID」固定命名（员工 staff_{id}.jpg / 顾客 cust_{id}.jpg），
    /// 同名覆盖自动替换旧图，无需数据库字段存储路径，前端按同一规则拼访问地址。
    /// 前缀不能省：员工与顾客的 ID 是两套独立序列（都从 1 开始），不加前缀会互相覆盖头像。
    /// </summary>
    [HttpPost("avatar")]
    [Authorize(Policy = Policies.AnyAuthenticated)]
    public async Task<IActionResult> UploadAvatar(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object> { Code = 400, Message = "请选择图片文件", Success = false });

        // 限制 2MB 以内、仅常见图片格式
        if (file.Length > 2 * 1024 * 1024)
            return BadRequest(new ApiResponse<object> { Code = 400, Message = "头像大小不能超过 2MB", Success = false });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp"))
            return BadRequest(new ApiResponse<object> { Code = 400, Message = "仅支持 jpg / png / webp 格式", Success = false });

        var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "avatars");
        Directory.CreateDirectory(dir);
        // 按身份分前缀：员工与顾客 ID 序列互不相干，同号不加前缀会覆盖对方的头像
        var userType = User.FindFirst("user_type")?.Value ?? "staff";
        var prefix = userType == "customer" ? "cust" : "staff";
        // 固定命名：不管上传的是什么格式，统一存为 {prefix}_{userId}.jpg（同名覆盖旧图）
        var fileName = $"{prefix}_{userId}.jpg";
        var fullPath = Path.Combine(dir, fileName);

        using (var stream = System.IO.File.Create(fullPath))
            await file.CopyToAsync(stream);

        var url = $"/uploads/avatars/{fileName}";
        return Ok(new ApiResponse<object> { Data = new { url }, Message = "头像已更新" });
    }
}

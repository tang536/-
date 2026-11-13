using System.Diagnostics.CodeAnalysis;
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
[SuppressMessage("Major Code Smell", "S6960:Controllers should have a single responsibility",
    Justification = "员工登录与顾客登录共用 api/auth 前缀，前端登录页/注册页与需求 4.2 接口清单都按这个路径对接；拆成两个控制器必须改路由前缀，对外契约会断。")]
public class AuthController(AuthService auth, SystemConfigService cfg) : ControllerBase
{
    ///<summary>用户类型默认值：门店员工（JWT 里没有 user_type 声明时按员工处理）</summary>
    private const string UserTypeStaff = "staff";

    private readonly AuthService _auth = auth;
    private readonly SystemConfigService _cfg = cfg;

    ///<summary>登录页公开配置：站点名称、可用登录方式、主题选项</summary>
    [AllowAnonymous]
    [HttpGet("options")]
    public async Task<IActionResult> Options() => Ok(new ApiResponse<object> { Data = await _cfg.GetPublic() });

    ///<summary>令牌续期：access token 临近过期时用它换一对新令牌，用户无需重新输入密码</summary>
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
            return Unauthorized(new ApiResponse<object> { Code = 401, Message = ErrorText.Safe(ex), Success = false });
        }
    }

    [AllowAnonymous]   //登录接口：此时必然没有 JWT，必须显式声明匿名（详见 RequireAuthorizationFilter）
    [HttpPost("staff-login")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> StaffLogin([FromBody] StaffLoginDto dto)
    {
        //真实客户端 IP：ForwardedHeaders 中间件已按「仅信任本机反代」还原 X-Forwarded-For
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        try { return Ok(new ApiResponse<LoginResponseDto> { Data = await _auth.StaffLogin(dto, ip) }); }
        catch (Exception ex) when (ErrorText.IsHandled(ex)) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    [AllowAnonymous]
    [HttpPost("customer-login")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> CustomerLogin([FromBody] CustomerLoginDto dto)
    {
        try { return Ok(new ApiResponse<LoginResponseDto> { Data = await _auth.CustomerLogin(dto) }); }
        catch (Exception ex) when (ErrorText.IsHandled(ex)) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    [AllowAnonymous]
    [HttpPost("customer-register")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> RegisterCustomer([FromBody] CustomerRegisterDto dto)
    {
        try
        {
            var c = await _auth.CustomerRegister(dto);
            return Ok(new ApiResponse<object> { Message = "注册成功", Data = new { c.CustomerId, c.LoginAccount, c.FullName } });
        }
        catch (Exception ex) when (ErrorText.IsHandled(ex)) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    ///<summary>顾客登录后自助补全租车资料（身份证号/手机号/真实姓名）</summary>
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
        catch (Exception ex) when (ErrorText.IsHandled(ex)) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    ///<summary>顾客自助修改登录密码：需提供原密码验证身份，新密码走复杂度校验（与注册同规则）</summary>
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
        catch (Exception ex) when (ErrorText.IsHandled(ex)) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    ///<summary>退出登录：把当前会话从单点登录登记表中撤销（同一账号不允许同时登录）</summary>
    [HttpPost("logout")]
    [Authorize(Policy = Policies.AnyAuthenticated)]
    public async Task<IActionResult> Logout()
    {
        var id = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var userType = User.FindFirst("user_type")?.Value ?? UserTypeStaff;
        await _auth.Logout(userType, id, User.FindFirst(Middleware.SingleSessionMiddleware.SessionClaim)?.Value);
        return Ok(new ApiResponse<object> { Message = "已退出登录" });
    }

    ///<summary>用户自主注销账号：登录用户注销自己（需二次输入密码）</summary>
    [HttpPost("deactivate")]
    [Authorize(Policy = Policies.AdminOrCustomer)]
    public async Task<IActionResult> DeactivateAccount([FromBody] DeactivateAccountDto dto)
    {
        try
        {
            var id = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var userType = User.FindFirst("user_type")?.Value ?? UserTypeStaff;
            await _auth.Deactivate(id, userType, dto.Password);
            return Ok(new ApiResponse<object> { Message = "账号已注销" });
        }
        catch (Exception ex) when (ErrorText.IsHandled(ex)) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    ///<summary>上传个人头像</summary>
    [HttpPost("avatar")]
    [Authorize(Policy = Policies.AnyAuthenticated)]
    public async Task<IActionResult> UploadAvatar(IFormFile file)
    {
        if (file.Length == 0) return BadRequest(new ApiResponse<object> { Code = 400, Message = "请选择图片文件", Success = false });

        // 头像不压缩、不再编码：直接把用户上传的原文件落盘。
        // 因此上限放宽到 10MB，避免用户为了绕过 2MB 限制先去压图，反而把画质弄丢。
        const long maxAvatarBytes = 10 * 1024 * 1024;
        if (file.Length > maxAvatarBytes) return BadRequest(new ApiResponse<object> { Code = 400, Message = "头像不能超过 10MB；系统不压缩原图，请直接上传原文件", Success = false });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp")) return BadRequest(new ApiResponse<object> { Code = 400, Message = "仅支持 jpg / jpeg / png / webp 格式", Success = false });

        var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "avatars");
        Directory.CreateDirectory(dir);
        //按身份分前缀：员工与顾客 ID 序列互不相干，同号不加前缀会覆盖对方的头像
        var userType = User.FindFirst("user_type")?.Value ?? UserTypeStaff;
        var prefix = userType == "customer" ? "cust" : UserTypeStaff;

        // 旧版本把任何格式都存成 .jpg（WebP/PNG 字节套 JPEG 后缀，MIME 与实际内容不符）。
        // 这里先清掉同一主体的所有旧后缀，再按原扩展名保存，保证原图字节与 MIME 一致。
        foreach (var oldExt in new[] { ".jpg", ".jpeg", ".png", ".webp" })
        {
            var oldPath = Path.Combine(dir, $"{prefix}_{userId}{oldExt}");
            if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
        }

        var fileName = $"{prefix}_{userId}{ext}";
        var fullPath = Path.Combine(dir, fileName);

        using (var stream = System.IO.File.Create(fullPath))
            await file.CopyToAsync(stream);

        var url = $"/uploads/avatars/{fileName}";
        return Ok(new ApiResponse<object> { Data = new { url }, Message = "头像已更新（原图未压缩）" });
    }
}

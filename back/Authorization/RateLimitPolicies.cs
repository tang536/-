namespace ERental.Authorization;

/// <summary>
/// 限流策略名。与 <c>Program.cs</c> 里 <c>AddRateLimiter</c> 注册的具名策略一一对应，
/// 控制器侧用 <c>[EnableRateLimiting(RateLimitPolicies.Auth)]</c> 引用，
/// 避免「注册名」与「引用名」各写一份字符串、改一处漏一处。
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>认证类接口：员工登录 / 顾客登录 / 注册 / 修改密码。每 IP 每分钟 10 次。</summary>
    public const string Auth = "auth";
}

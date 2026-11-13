namespace ERental.Data;

/// <summary>
/// 应用侧唯一时间源：业务代码取「现在」一律走这里，不再直接写 <c>DateTime.Now</c>。
/// <para>
/// 为什么必须收口成一个入口：
/// </para>
/// <list type="bullet">
/// <item><description>
/// 时区口径只有一个。数据库会话时区由 <c>MySqlSessionTimeZoneInterceptor</c> 对齐到应用本地时区，
/// 启动时 <see cref="DatabaseClock"/> 再校验一次；而 <c>v_order_overview</c> 的「是否逾期」用
/// MySQL 的 <c>NOW()</c> 算，服务层的「现在」用这里算。两边只要不是同一个时间基，
/// 同一条订单就会「列表里逾期、结算试算不逾期」——这类问题只能靠同源来避免。
/// </description></item>
/// <item><description>
/// 将来真要改口径（比如整体改成 UTC 存储、或按门店所在地而非服务器时区计算），
/// 改这一处即可，不用再去 80 多个调用点里逐个判断哪些该改。
/// </description></item>
/// <item><description>
/// 单元测试可用 <see cref="TestOverride"/> 固定时间，不必等真实时钟走到某个边界。
/// </description></item>
/// </list>
/// </summary>
internal static class AppClock
{
    /// <summary>
    /// 仅测试使用：非 null 时 <see cref="Now"/> / <see cref="Today"/> / <see cref="UtcNow"/> 全由它提供。
    /// 生产代码不得赋值——它是进程级静态状态，改了会影响所有并发请求。
    /// </summary>
    internal static Func<DateTime>? TestOverride { get; set; }

    /// <summary>当前本地时间（与数据库会话同一时区口径；用于租期、逾期、结算、日志等业务时间）</summary>
    public static DateTime Now => TestOverride?.Invoke() ?? DateTime.Now;

    /// <summary>今天（本地时区的日期，已去掉时分秒；用于「按天统计」「今天到期」这类判断）</summary>
    public static DateTime Today => Now.Date;

    /// <summary>
    /// 当前 UTC 时间。只用于必须与外部协议对齐的场景（JWT 过期时间、HTTP 头、支付签名），
    /// 业务逻辑一律用 <see cref="Now"/>，避免「本地时间当成 UTC 用」的隐性错位。
    /// </summary>
    public static DateTime UtcNow => TestOverride?.Invoke().ToUniversalTime() ?? DateTime.UtcNow;
}

using System.Data;
using Microsoft.EntityFrameworkCore;

namespace ERental.Data;

/// <summary>
/// 启动自检：确认「数据库会话时区」与「应用进程本地时区」是同一个口径。
/// <para>
/// v_order_overview 的 is_overdue / billable_hours 用 NOW() 计算，而服务层用 DateTime.Now
/// 判定「今天到期」「逾期几个小时」。两者只要偏移不同，同一条订单会在列表里显示逾期、
/// 在结算试算里却不逾期（或反过来）。连接建立时已由 <see cref="MySqlSessionTimeZoneInterceptor"/>
/// 统一会话时区；这里再读一次数据库时钟做端到端校验，避免拦截器未注册、连接被代理复用
/// 或驱动忽略 SET time_zone 时静默带病运行。
/// </para>
/// </summary>
internal static class DatabaseClock
{
    /// <summary>允许的误差：连接建立与查询之间可能跨过整分钟边界</summary>
    internal const int ToleranceSeconds = 60;

    /// <summary>校验失败直接终止启动：带病运行会让逾期/计费结果不一致，比启动失败更难排查</summary>
    public static void EnsureSessionMatchesLocalTime(AppDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) connection.Open();

        try
        {
            using var cmd = connection.CreateCommand();
            // TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(), NOW()) = 本地时区相对 UTC 的偏移（秒）
            cmd.CommandText = "SELECT TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(), NOW())";
            var value = cmd.ExecuteScalar();
            if (value is null or DBNull)
                throw new InvalidOperationException("数据库未返回会话时区偏移，无法校验时间口径");

            var databaseOffsetSeconds = Convert.ToInt32(value);
            var appOffsetSeconds = (int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.Now).TotalSeconds;

            if (!IsAligned(appOffsetSeconds, databaseOffsetSeconds))
                throw new InvalidOperationException(
                    $"数据库会话时区与应用时区不一致（应用 UTC{FormatOffset(appOffsetSeconds)} / 数据库 UTC{FormatOffset(databaseOffsetSeconds)}）。" +
                    "请确认 MySqlSessionTimeZoneInterceptor 已注册到 DbContext，且连接串没有被会固定时区的代理接管；" +
                    "应用不会依赖 MySQL 的全局 time_zone 设置。");

            Console.WriteLine($"[时间口径] 数据库会话时区与应用一致：UTC{FormatOffset(appOffsetSeconds)}");
        }
        finally
        {
            if (openedHere) connection.Close();
        }
    }

    /// <summary>偏移对齐判断（抽出纯函数便于单元测试）</summary>
    internal static bool IsAligned(int appOffsetSeconds, int databaseOffsetSeconds, int toleranceSeconds = ToleranceSeconds)
        => Math.Abs(appOffsetSeconds - databaseOffsetSeconds) <= toleranceSeconds;

    private static string FormatOffset(int seconds)
    {
        var minutes = seconds / 60;
        var sign = minutes < 0 ? '-' : '+';
        minutes = Math.Abs(minutes);
        return $"{sign}{minutes / 60:00}:{minutes % 60:00}";
    }
}

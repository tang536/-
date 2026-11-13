using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ERental.Data;

/// <summary>
/// 把 MySQL 会话时区同步为应用进程的本地时区。
/// <para>
/// v_order_overview 里的 <c>is_overdue</c>、<c>billable_hours</c> 使用数据库的 NOW()，
/// 而应用层的「今天」「逾期预警」使用 DateTime.Now。两者只有在数据库会话时区
/// 与应用时区一致时才会得到相同结论。连接串不保证这一点（MySQL 默认跟随服务端
/// 的 system_time_zone），因此每次物理连接建立后显式设置一次会话时区。
/// </para>
/// </summary>
public sealed class MySqlSessionTimeZoneInterceptor : DbConnectionInterceptor
{
    /// <summary>当前进程本地时区相对 UTC 的偏移，格式为 MySQL 接受的 ±HH:MM</summary>
    public static string CurrentOffset() => FormatOffset(TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.Now));

    /// <summary>把 UTC 偏移量格式化为 MySQL 的 ±HH:MM；偏移量为负时保留负号</summary>
    public static string FormatOffset(TimeSpan offset)
    {
        var minutes = (int)offset.TotalMinutes;
        var sign = minutes < 0 ? '-' : '+';
        minutes = Math.Abs(minutes);
        return $"{sign}{minutes / 60:00}:{minutes % 60:00}";
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => ApplySessionTimeZone(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => ApplySessionTimeZoneAsync(connection, cancellationToken);

    /// <summary>
    /// 对任意已打开的连接应用会话时区。
    /// EF 拦截器会自动调用；独立连接（对象安装器）也必须显式调用一次，
    /// 否则它写入的 NOW() 会退回 MySQL 服务端的 system_time_zone。
    /// </summary>
    internal static void ApplySessionTimeZone(DbConnection connection)
    {
        using var cmd = CreateCommand(connection);
        cmd.ExecuteNonQuery();
    }

    private static async Task ApplySessionTimeZoneAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = CreateCommand(connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DbCommand CreateCommand(DbConnection connection)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SET time_zone = @tz";
        var p = cmd.CreateParameter();
        p.ParameterName = "@tz";
        p.Value = CurrentOffset();
        cmd.Parameters.Add(p);
        return cmd;
    }
}

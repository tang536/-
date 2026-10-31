using System.Net.Sockets;
using System.Reflection;

namespace ERental.Services;

/// <summary>并发冲突（等锁超时 / 数据被他人抢先修改）的业务异常</summary>
public sealed class ConcurrentOperationException(string message) : Exception(message);

/// <summary>数据库并发冲突的识别与重试</summary>
public static class DbConcurrency
{
    /// <summary>InnoDB 死锁：当前事务被选为牺牲者并已回滚</summary>
    private const int ErDeadlock = 1213;

    /// <summary>等锁超时（由 压到 5 秒）</summary>
    private const int ErLockWaitTimeout = 1205;

    /// <summary>唯一键冲突：本项目用它作为「一单只能结算一次」的最后一道防线</summary>
    private const int ErDuplicateEntry = 1062;

    private const int DefaultAttempts = 3;

    /// <summary>等锁超时时给用户看的话</summary>
    private const string BusyMessage = "该单据正在被其他同事处理，请稍后刷新后重试";

    /// <summary>死锁（事务已被回滚，可整段重放）</summary>
    public static bool IsDeadlock(Exception ex)
    {
        foreach (var e in Walk(ex))
        {
            if (ReadNumber(e) is ErDeadlock) return true;
            if (e.Message.Contains("Deadlock found", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>等锁超时（持锁者仍在事务中，重试没有意义）</summary>
    public static bool IsLockWaitTimeout(Exception ex)
    {
        foreach (var e in Walk(ex))
        {
            if (ReadNumber(e) is ErLockWaitTimeout) return true;
            if (e.Message.Contains("Lock wait timeout exceeded", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>是否属于「可以整段重放」的并发冲突</summary>
    public static bool IsRetryable(Exception ex) => IsDeadlock(ex);

    /// <summary>该异常是否是唯一键冲突（调用方据此把「重复写入」翻译成业务文案）</summary>
    public static bool IsDuplicateKey(Exception ex)
    {
        foreach (var e in Walk(ex))
        {
            if (ReadNumber(e) is ErDuplicateEntry) return true;
            if (e.Message.Contains("Duplicate entry", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>执行一段「自带事务」的操作：遇到死锁整段重放，遇到等锁超时立即失败</summary>
    /// 重试前的清理动作（通常是 <c>() =&gt; db.ChangeTracker.Clear()</c>）。
    /// 事务虽然已回滚，但 DbContext 的变更跟踪器仍保留上次的临时状态，不清掉会重复提交。
    public static async Task<T> RunAsync<T>(
        Func<Task<T>> action, ILogger logger, string scope,
        int attempts = DefaultAttempts, Action? reset = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (IsDeadlock(ex) && attempt < attempts)
            {
                // 事务已回滚，但 DbContext 的变更跟踪器还记着上次的临时状态（新增实体、已改属性），
                // 不清掉的话重放时会把同一批写入再提交一遍。
                reset?.Invoke();

                // 抖动退避：20ms / 40ms 再叠 0~20ms 随机，避免两个冲突方按同一节奏再次对撞
                var delay = (20 * attempt) + Random.Shared.Next(0, 20);
                logger.LogWarning(ex,
                    "{Scope} 遇到死锁（第 {Attempt}/{Total} 次），{Delay}ms 后整段重试",
                    scope, attempt, attempts, delay);
                await Task.Delay(delay);
            }
            catch (Exception ex) when (IsLockWaitTimeout(ex))
            {
                // 不再重试：锁还压在别人手里，再等 10 秒只会把「5 秒知道结果」拖成「15 秒后超时」。
                logger.LogWarning(ex, "{Scope} 等锁超时，判定为并发冲突，直接返回业务提示", scope);
                throw new ConcurrentOperationException(BusyMessage);
            }
        }
    }

    /// <summary>无返回值的重载</summary>
    public static async Task RunAsync(
        Func<Task> action, ILogger logger, string scope,
        int attempts = DefaultAttempts, Action? reset = null)
        => await RunAsync(async () => { await action(); return true; }, logger, scope, attempts, reset);

    /// <summary>逐层展开 InnerException（EF 常把驱动异常包在 DbUpdateException / Invali…</summary>
    private static IEnumerable<Exception> Walk(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException) yield return e;
    }

    /// <summary>读驱动的错误码</summary>
    private static int? ReadNumber(Exception ex)
    {
        var prop = ex.GetType().GetProperty("Number", BindingFlags.Public | BindingFlags.Instance);
        if (prop?.PropertyType == typeof(int) && prop.GetValue(ex) is int n) return n;
        return null;
    }

    /// <summary>瞬时数据库连接故障（连接被服务端/中间件掐断、网络抖动、读取超时）的错误码</summary>
    private static readonly HashSet<int> TransientMySqlNumbers = new() { 0, 2006, 2013, 2055, 1205 };

    /// <summary>判断异常是否来自"连接层瞬时故障"（而非业务错误），据此决定是否重试</summary>
    public static bool IsTransientConnection(Exception ex)
    {
        foreach (var e in Walk(ex))
        {
            if (e is SocketException or IOException or TimeoutException) return true;
            if (ReadNumber(e) is { } n && TransientMySqlNumbers.Contains(n)) return true;
            if (e.Message.Contains("server has gone away", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("Lost connection", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>对可能遭遇连接层瞬时故障的"只读"操作整段重试</summary>
    public static async Task<T> RetryTransientAsync<T>(
        Func<Task<T>> action, ILogger logger, string scope, int attempts = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (IsTransientConnection(ex) && attempt < attempts)
            {
                // 抖动退避：50ms / 100ms 再叠 0~50ms 随机，避免多个并发请求同步重试对库造成冲击
                var delay = (50 * attempt) + Random.Shared.Next(0, 50);
                logger.LogWarning(ex,
                    "{Scope} 遇到瞬时数据库连接故障（第 {Attempt}/{Total} 次），{Delay}ms 后重试",
                    scope, attempt, attempts, delay);
                await Task.Delay(delay);
            }
        }
    }
}

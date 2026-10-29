using System.Reflection;

namespace ERental.Services;

/// <summary>
/// 并发冲突（等锁超时 / 数据被他人抢先修改）的业务异常。
///
/// 单独定义类型而不是 <c>throw new Exception("中文")</c>，是为了让
/// <see cref="ErrorText.Safe"/> 能明确区分「这是我们自己写的可读文案」与
/// 「驱动抛上来的英文原文」——后者绝不能下发到前端。
/// </summary>
public sealed class ConcurrentOperationException(string message) : Exception(message);

/// <summary>
/// 数据库并发冲突的识别与重试。
///
/// 引入行级锁之后，「等锁超时」和「死锁」从理论问题变成了**必然会发生**的正常事件。
/// 但两者性质完全不同，处理方式也必须分开：
///
///   ① 死锁（1213）：InnoDB 检测到环形等待，已经**主动回滚了其中一方**的事务。
///      数据库处于干净状态，重放整段事务是安全的，重试有效 → 最多 3 次。
///   ② 等锁超时（1205）：持锁者的**事务还在跑**（典型场景：结算事务里正在调支付网关）。
///      重试时锁依然被占着，几乎必然再次超时，而每次都要等满 5 秒——三次就把前端
///      axios 的 15 秒超时耗尽（front/src/api/index.ts），用户只看到「请求超时，
///      请检查网络」，真实原因（同事正在处理这张单）传不到界面上。
///      因此等锁超时**不重试**，直接翻译成「该单据正在被其他同事处理，请稍后刷新后重试」。
///
/// 为什么必须重放整段事务而不是只重试那条 SQL：
/// 死锁回滚的是整个事务，事务内已经执行过的写入全部失效；若只重试最后一句，
/// 会写出一半的数据（例如订单已置「已完结」但结算单被回滚）。
/// </summary>
public static class DbConcurrency
{
    /// <summary>InnoDB 死锁：当前事务被选为牺牲者并已回滚。</summary>
    private const int ErDeadlock = 1213;

    /// <summary>等锁超时（由 <see cref="DbRowLock.PrepareAsync"/> 压到 5 秒）。</summary>
    private const int ErLockWaitTimeout = 1205;

    /// <summary>唯一键冲突：本项目用它作为「一单只能结算一次」的最后一道防线。</summary>
    private const int ErDuplicateEntry = 1062;

    private const int DefaultAttempts = 3;

    /// <summary>等锁超时时给用户看的话。前端会把这句话原样弹出来。</summary>
    private const string BusyMessage = "该单据正在被其他同事处理，请稍后刷新后重试";

    /// <summary>死锁（事务已被回滚，可整段重放）。</summary>
    public static bool IsDeadlock(Exception ex)
    {
        foreach (var e in Walk(ex))
        {
            if (ReadNumber(e) is ErDeadlock) return true;
            if (e.Message.Contains("Deadlock found", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>等锁超时（持锁者仍在事务中，重试没有意义）。</summary>
    public static bool IsLockWaitTimeout(Exception ex)
    {
        foreach (var e in Walk(ex))
        {
            if (ReadNumber(e) is ErLockWaitTimeout) return true;
            if (e.Message.Contains("Lock wait timeout exceeded", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>是否属于「可以整段重放」的并发冲突。只剩死锁一种。</summary>
    public static bool IsRetryable(Exception ex) => IsDeadlock(ex);

    /// <summary>该异常是否是唯一键冲突（调用方据此把「重复写入」翻译成业务文案）。</summary>
    public static bool IsDuplicateKey(Exception ex)
    {
        foreach (var e in Walk(ex))
        {
            if (ReadNumber(e) is ErDuplicateEntry) return true;
            if (e.Message.Contains("Duplicate entry", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 执行一段「自带事务」的操作：遇到死锁整段重放，遇到等锁超时立即失败。
    /// <paramref name="action"/> 内部必须自己 <c>BeginTransaction</c> 并在异常时回滚，
    /// 这样每次重放都是一次干净的事务。
    /// </summary>
    /// <param name="action">要执行的操作（每次调用都应是全新的 DbContext 状态或已回滚的事务）</param>
    /// <param name="logger">日志；冲突重试会以 Warning 记录，便于观察真实并发压力</param>
    /// <param name="scope">日志里的场景名，如「订单结算」</param>
    /// <param name="attempts">最多尝试次数（含首次），只对死锁生效</param>
    /// <param name="reset">
    /// 重试前的清理动作（通常是 <c>() =&gt; db.ChangeTracker.Clear()</c>）。
    /// 事务虽然已回滚，但 DbContext 的变更跟踪器仍保留上次的临时状态，不清掉会重复提交。
    /// </param>
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

    /// <summary>无返回值的重载。</summary>
    public static async Task RunAsync(
        Func<Task> action, ILogger logger, string scope,
        int attempts = DefaultAttempts, Action? reset = null)
        => await RunAsync(async () => { await action(); return true; }, logger, scope, attempts, reset);

    /// <summary>逐层展开 InnerException（EF 常把驱动异常包在 DbUpdateException / InvalidOperationException 里）。</summary>
    private static IEnumerable<Exception> Walk(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException) yield return e;
    }

    /// <summary>
    /// 读驱动的错误码。用反射而不是直接引用 MySqlException：
    /// 本项目曾因换驱动（Pomelo → Oracle 官方）整体改过数据访问层，
    /// 把错误码判定与具体驱动类型解耦，将来再换驱动时这段不用跟着改。
    /// </summary>
    private static int? ReadNumber(Exception ex)
    {
        var prop = ex.GetType().GetProperty("Number", BindingFlags.Public | BindingFlags.Instance);
        if (prop?.PropertyType == typeof(int) && prop.GetValue(ex) is int n) return n;
        return null;
    }
}

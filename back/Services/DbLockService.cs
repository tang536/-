using System.Data;
using System.Text.RegularExpressions;
using ERental.Data;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>
/// 跨进程/跨实例的互斥锁，基于 MySQL 的 <c>GET_LOCK</c>。
///
/// 为什么需要它：定时任务（每日备份、待支付超时回收）是进程内计时器，
/// 部署两台以上机器时每台都会各跑一遍，出现「同一时刻两份备份」「重复回收同一批订单」。
/// 引入 Redis/ZooKeeper 对门店规模是过度设计，MySQL 命名锁是零新增组件的最小方案：
/// 锁随连接存在，进程崩溃或连接断开时数据库自动释放，不存在死锁残留。
///
/// 使用约束：锁必须在**同一条连接**上获取与释放，所以本服务在持锁期间独占该连接，
/// 调用方应在 using 作用域内完成整段任务，作用域外不要再碰同一个 DbContext。
/// </summary>
public partial class DbLockService(AppDbContext db, ILogger<DbLockService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly ILogger<DbLockService> _logger = logger;

    /// <summary>GET_LOCK 的名字上限是 64 字符，且允许的字符集很窄，这里先做白名单校验，非法直接拒绝（防注入，也防写错）</summary>
    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$")]
    private static partial Regex LockNamePattern();

    /// <summary>
    /// 尝试获取命名锁。
    /// </summary>
    /// <param name="name">锁名，仅允许字母数字下划线，如 <c>erental_backup</c></param>
    /// <param name="wait">等待时长；传 <see cref="TimeSpan.Zero"/> 表示拿不到就立即返回</param>
    /// <param name="ct">取消令牌（仅作用于开连接与查询本身，不影响锁的生命周期）</param>
    /// <returns>拿到返回句柄（Dispose 时释放），拿不到返回 <c>null</c>（不是异常，正常的多实例让位）</returns>
    public async Task<DbLockHandle?> TryAcquireAsync(string name, TimeSpan wait, CancellationToken ct = default)
    {
        if (!LockNamePattern().IsMatch(name))
            throw new ArgumentException($"锁名不合法：{name}", nameof(name));

        var conn = _db.Database.GetDbConnection();
        var openedHere = false;
        try
        {
            if (conn.State != ConnectionState.Open)
            {
                await _db.Database.OpenConnectionAsync(ct);
                openedHere = true;
            }

            // ⚠ 防「递归计数」：MySQL 对**同一连接**重复 GET_LOCK 同名锁不会失败，
            // 而是把持有计数 +1。若上一轮遗留一次没释放，本次 GET_LOCK 照样返回 1，
            // 之后 RELEASE_LOCK 只减 1（计数 2→1），日志看起来「已释放」，锁其实还在手里，
            // 另一台机器就永远拿不到这把锁。
            // 拿锁前先无条件 RELEASE 一次（不是本连接持有则返回 0，无副作用）。
            // 约束：同一 scope 内不要嵌套获取同名锁，本服务的两个调用点都是「一次性任务」。
            await ScalarAsync(conn, $"SELECT RELEASE_LOCK('{name}')", ct);

            // name 已通过白名单校验，可安全内联；超时取整秒（GET_LOCK 只接受整数秒）
            var seconds = (int)Math.Clamp(Math.Ceiling(wait.TotalSeconds), 0, 3600);
            var got = await ScalarAsync(conn, $"SELECT GET_LOCK('{name}', {seconds})", ct);

            // 1=拿到 0=超时 NULL=出错（如锁名超长、权限不足）
            if (got is null or not 1)
            {
                if (openedHere) await _db.Database.CloseConnectionAsync();
                return null;
            }

            return new DbLockHandle(this, conn, name, openedHere);
        }
        catch
        {
            if (openedHere)
            {
                try { await _db.Database.CloseConnectionAsync(); } catch { /* 关连接失败不掩盖原始异常 */ }
            }
            throw;
        }
    }

    /// <summary>在当前连接上执行一条返回单值的 SQL</summary>
    private static async Task<object?> ScalarAsync(
        System.Data.Common.DbConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct);
    }

    /// <summary>
    /// 释放锁。
    ///
    /// ── 为什么不能只写一句 RELEASE_LOCK ──
    /// <c>GET_LOCK</c> 的生命周期绑在**物理连接**上，而 <c>CloseConnectionAsync</c> 只是把连接
    /// 归还给连接池——物理 TCP 连接不断开，锁也就不会释放。于是有三类泄漏：
    ///   ① 释放时连接恰好不是 Open（被上层关掉、或异常路径提前归还），旧写法直接跳过 RELEASE，
    ///      锁跟着这条连接留在池里，谁复用到它谁就"白捡"了一把锁，真正的持锁者永远等不到；
    ///   ② RELEASE 抛异常只记 warning 然后照常归还池，同上；
    ///   ③ 同一连接重复 GET_LOCK 会累加计数，只 RELEASE 一次减不到 0。
    /// 这里逐条堵：连接关了就重新打开再释放、循环释放到计数归零、
    /// 释放后用 <c>IS_USED_LOCK</c> 回读校验，仍被持有则**主动清掉连接池里的物理连接**，
    /// 由 MySQL 在 TCP 断开时兜底释放——宁可下次重建连接，也不能留一把幽灵锁。
    /// </summary>
    internal async Task ReleaseAsync(System.Data.Common.DbConnection conn, string name, bool closeConnection)
    {
        var stillHeld = false;
        try
        {
            // ① 连接已归还也不能跳过：重新打开后释放的是同一条连接上的锁
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            // ③ 递归计数：释放到「锁不存在」或「不是本连接持有」为止
            for (var i = 0; i < 8; i++)
            {
                var r = await ScalarAsync(conn, $"SELECT RELEASE_LOCK('{name}')", CancellationToken.None);
                if (r is null or DBNull) break;                 // 锁不存在，已干净
                if (Convert.ToInt32(r) == 0) break;             // 不是本连接持有，无需再减
            }

            // 回读校验：IS_USED_LOCK 返回持有者的连接 id，NULL 表示无人持有
            var owner = await ScalarAsync(conn, $"SELECT IS_USED_LOCK('{name}')", CancellationToken.None);
            if (owner is not null and not DBNull)
            {
                stillHeld = true;
                _logger.LogError("数据库锁 {Name} 释放后仍被连接 {Owner} 持有，将清除连接池中的物理连接", name, owner);
            }
        }
        catch (Exception ex)
        {
            stillHeld = true;
            _logger.LogError(ex, "释放数据库锁 {Name} 失败，将清除连接池中的物理连接以强制回收", name);
        }
        finally
        {
            // ② 释放不干净就不许归还池：只 Close 等于把锁原封不动放回池里
            if (stillHeld) ClearPool(conn);

            if (closeConnection)
            {
                try { await _db.Database.CloseConnectionAsync(); }
                catch { /* 忽略：连接可能已被上层释放 */ }
            }
        }
    }

    /// <summary>
    /// 清掉连接池里的物理连接，让 MySQL 在 TCP 断开时自动释放 <c>GET_LOCK</c>。
    ///
    /// 用反射调用驱动自带的 <c>ClearPool</c>（Oracle 官方驱动是
    /// <c>MySqlConnection.ClearPool(MySqlConnection)</c>）：项目刻意不在业务代码里
    /// 强引用具体驱动类型（历史上有过 Pomelo → Oracle 的整体替换），
    /// 反射失败就退化成「打日志 + 等连接被池淘汰」，不会因为清理失败而抛出新异常。
    /// </summary>
    private void ClearPool(System.Data.Common.DbConnection conn)
    {
        try
        {
            // 精确挑出「静态 + 单个参数且能接受当前连接类型」的那个重载，
            // 避免命中 ClearPoolAsync / 带 ClearPoolOptions 的重载而参数不匹配
            var method = conn.GetType()
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "ClearPool"
                                     && m.GetParameters().Length == 1
                                     && m.GetParameters()[0].ParameterType.IsInstanceOfType(conn));
            if (method is null)
            {
                _logger.LogWarning("当前驱动没有 ClearPool，锁 {Connection} 上的命名锁将在连接被池淘汰后释放",
                    conn.GetType().Name);
                return;
            }
            method.Invoke(null, [conn]);
            _logger.LogWarning("已清除连接池中的物理连接，命名锁由服务端随连接断开自动回收");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清除连接池失败，命名锁将在该连接被池淘汰后释放");
        }
    }
}

/// <summary>持锁句柄：Dispose 即释放，配合 <c>await using</c> 使用</summary>
public sealed class DbLockHandle(
    DbLockService owner, System.Data.Common.DbConnection conn, string name, bool closeConnection)
    : IAsyncDisposable, IDisposable
{
    private readonly DbLockService _owner = owner;
    private readonly System.Data.Common.DbConnection _conn = conn;
    private readonly bool _closeConnection = closeConnection;
    private bool _released;

    /// <summary>锁名，便于日志标注</summary>
    public string Name { get; } = name;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_released) return;
        _released = true;
        await _owner.ReleaseAsync(_conn, Name, _closeConnection);
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

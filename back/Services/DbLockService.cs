using System.Data;
using System.Data.Common;
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
/// ⚠ 用一个**独占连接**取锁，而不是复用 EF 的连接池连接。
/// 池化连接被「关闭」时只是还池，物理 TCP 还在，锁就跟着那条连接留在池里——
/// 表现为「锁没人用却一直显示被占用，下一次抢锁永远失败」：线上备份任务每次都判定
/// 「已由其他实例执行」直接跳过，`/var/backups/erental` 里一份 .erbk 都没有。
/// 这里的做法是：锁连接强制 <c>Pooling=false</c>，用完销毁物理连接，服务端必然回收锁；
/// 即使 RELEASE_LOCK 因网络问题没生效，断开连接也能兜住。
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
    /// <param name="ct">取消令牌（仅作用于建连与取锁本身，不影响锁的生命周期）</param>
    /// <returns>拿到返回句柄（Dispose 时释放），拿不到返回 <c>null</c>（不是异常，正常的多实例让位）</returns>
    public async Task<DbLockHandle?> TryAcquireAsync(string name, TimeSpan wait, CancellationToken ct = default)
    {
        if (!LockNamePattern().IsMatch(name))
            throw new ArgumentException($"锁名不合法：{name}", nameof(name));

        var conn = CreateLockConnection();
        var opened = false;
        try
        {
            await conn.OpenAsync(ct);
            opened = true;

            // ⚠ 防「递归计数」：MySQL 对**同一连接**重复 GET_LOCK 同名锁不会失败，
            // 而是把持有计数 +1。若上一轮遗留一次没释放，本次 GET_LOCK 照样返回 1，
            // 之后 RELEASE_LOCK 只减 1（计数 2→1），日志看起来「已释放」，锁其实还在手里。
            // 拿锁前先无条件 RELEASE 一次（不是本连接持有则返回 0，无副作用）。
            await ScalarAsync(conn, $"SELECT RELEASE_LOCK('{name}')", ct);

            // name 已通过白名单校验，可安全内联；超时取整秒（GET_LOCK 只接受整数秒）
            var seconds = (int)Math.Clamp(Math.Ceiling(wait.TotalSeconds), 0, 3600);
            var got = await ScalarAsync(conn, $"SELECT GET_LOCK('{name}', {seconds})", ct);

            // 1=拿到 0=超时 NULL=出错（如锁名超长、权限不足）
            // ⚠ 必须用 Convert 判值：驱动把 GET_LOCK 的结果当 BIGINT 返回（装箱 long），
            // 写成 `got is not 1` 时模式里的 1 是 int，装箱 long 永远不匹配 int 常量，
            // 于是「拿到了锁」被当成「没拿到」——锁在手却让位，且句柄为 null 不会走释放路径，
            // 锁就一直挂在这条连接上，下一次自己抢自己失败。线上每日备份从来没生成过文件，根因就在这里。
            if (got is null or DBNull || Convert.ToInt64(got) != 1)
            {
                if (got is null or DBNull)
                    _logger.LogWarning("获取数据库锁 {Name} 返回 NULL（锁名非法或权限不足），本轮跳过", name);
                await SafeCloseAsync(conn);
                return null;
            }

            return new DbLockHandle(this, conn, name);
        }
        catch
        {
            if (opened) await SafeCloseAsync(conn); else conn.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 建一条**专供取锁**的连接：复制应用连接串，强制关掉连接池。
    ///
    /// 关池是关键——池化连接 Dispose 时只是还池，物理连接与它持有的命名锁都留在池里，
    /// 于是锁被一条没人使用的连接长期占住（线上实测：备份每次判定「其他实例在执行」，备份文件一个都没生成）。
    /// 关池后 Dispose 会真正断开 TCP，MySQL 随之释放该连接上的全部命名锁。
    ///
    /// 反射创建同类型连接是为了不在业务代码里强引用具体驱动（历史上有过 Pomelo → Oracle 的整体替换）；
    /// 反射失败时退回 EF 的连接串与原连接，锁仍可用，但「释放后残留」只能靠 ReleaseAsync 里的强制断连兜底。
    /// </summary>
    private DbConnection CreateLockConnection()
    {
        var cs = _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            cs = _db.Database.GetDbConnection().ConnectionString;
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("数据库连接串为空，无法建立锁连接");

        // 统一改写成「不池化」：驱动可能是 MySql.Data 也可能是 MySqlConnector，两家都认这个键
        var builder = new DbConnectionStringBuilder { ConnectionString = cs };
        builder["Pooling"] = false;
        var lockCs = builder.ConnectionString;

        try
        {
            var prototype = _db.Database.GetDbConnection();
            if (prototype.GetType().GetConstructor(Type.EmptyTypes) is null)
                throw new MissingMethodException(prototype.GetType().Name, ".ctor()");
            var conn = (DbConnection)Activator.CreateInstance(prototype.GetType())!;
            conn.ConnectionString = lockCs;
            return conn;
        }
        catch (Exception ex)
        {
            // 退回 EF 自己的连接：不理想（可能走连接池），但比没有锁好。ReleaseAsync 会回读校验并强制断连。
            _logger.LogWarning(ex, "无法创建独立的锁连接，退回使用 EF 连接（释放后仍被持有将强制断连）");
            var fallback = _db.Database.GetDbConnection();
            if (fallback.State != ConnectionState.Open) fallback.ConnectionString = lockCs;
            return fallback;
        }
    }

    /// <summary>在当前连接上执行一条返回单值的 SQL</summary>
    private static async Task<object?> ScalarAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct);
    }

    /// <summary>关连接但不让异常盖住原始错误；Pooling=false 时 Dispose 会真正断开 TCP</summary>
    private static async Task SafeCloseAsync(DbConnection conn)
    {
        try { await conn.CloseAsync(); } catch { /* 连接可能已断 */ }
        try { conn.Dispose(); } catch { /* 同上 */ }
    }

    /// <summary>
    /// 自检：这把锁此刻是否**仍由这条连接持有**。
    ///
    /// ── 为什么要这个（GET_LOCK 的固有短板）──
    /// <c>GET_LOCK</c> 的生命周期绑在物理连接上，连接一断锁就自动释放。这是它「不会死锁」的优点，
    /// 但反过来也意味着：长任务跑到中途，若连接被 MySQL 以 <c>wait_timeout</c> 掐掉，
    /// 锁会在**持有者毫不知情**的情况下消失，另一个实例随即拿到锁、
    /// 两个进程同时认为自己是主——正是「分布式锁依赖数据库连接」这条风险的落点。
    /// 无法改掉这个语义（换了 Redis 也一样有租约过期的同类问题），能做的是让持有者
    /// **主动发现**锁已丢失，而不是等到数据写重了才回头查。
    ///
    /// 判定用 <c>IS_USED_LOCK()</c> 返回的持有者连接 id 与本连接的
    /// <c>CONNECTION_ID()</c> 比对：只判断「有没有人持有」是不够的，
    /// 别人持有也是「有」，那种情况必须算丢失。
    /// </summary>
    internal async Task<bool> StillHeldAsync(DbConnection conn, string name)
    {
        try
        {
            if (conn.State != ConnectionState.Open) return false;
            var owner = await ScalarAsync(conn, $"SELECT IS_USED_LOCK('{name}')", CancellationToken.None);
            if (owner is null or DBNull) return false;
            var self = await ScalarAsync(conn, "SELECT CONNECTION_ID()", CancellationToken.None);
            return self is not null and not DBNull
                   && Convert.ToInt64(owner) == Convert.ToInt64(self);
        }
        catch (Exception ex)
        {
            // 查询失败本身就是「无法确认仍持有」：按丢失上报，让调用方走安全分支（让位/重拿）
            _logger.LogWarning(ex, "自检数据库锁 {Name} 状态失败，按未持有处理", name);
            return false;
        }
    }

    /// <summary>
    /// 释放锁。
    ///
    /// 两道都要：先 RELEASE_LOCK 做干净归还（计数可能大于 1，循环减到 0），
    /// 再销毁连接（<c>Pooling=false</c>，Dispose 即断开 TCP，MySQL 立刻回收该连接上的全部命名锁）。
    /// 只做后者也能释放，但日志里分不清是正常归还还是异常兜底；只做前者则扛不住网络抖动。
    /// </summary>
    internal async Task ReleaseAsync(DbConnection conn, string name)
    {
        try
        {
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            // 递归计数：释放到「锁不存在」或「不是本连接持有」为止
            for (var i = 0; i < 8; i++)
            {
                var r = await ScalarAsync(conn, $"SELECT RELEASE_LOCK('{name}')", CancellationToken.None);
                if (r is null or DBNull) break;                 // 锁不存在，已干净
                if (Convert.ToInt32(r) == 0) break;             // 不是本连接持有，无需再减
            }

            // 回读校验：IS_USED_LOCK 返回持有者的连接 id，NULL 表示无人持有
            var owner = await ScalarAsync(conn, $"SELECT IS_USED_LOCK('{name}')", CancellationToken.None);
            if (owner is not null and not DBNull)
                _logger.LogError("数据库锁 {Name} 释放后仍被连接 {Owner} 持有，将断开连接强制回收", name, owner);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "释放数据库锁 {Name} 失败，将断开连接强制回收", name);
        }
        finally
        {
            await SafeCloseAsync(conn);
        }
    }
}

/// <summary>
/// 持锁句柄：Dispose 即释放，配合 <c>await using</c> 使用。
/// 句柄独占一条 <c>Pooling=false</c> 的连接，不会被连接池复用，也不会把锁遗留在池里。
/// </summary>
public sealed class DbLockHandle(DbLockService owner, DbConnection conn, string name)
    : IAsyncDisposable, IDisposable
{
    private readonly DbLockService _owner = owner;
    private readonly DbConnection _conn = conn;
    private bool _released;

    /// <summary>锁名，便于日志标注</summary>
    public string Name { get; } = name;

    /// <summary>
    /// 自检：锁是否仍由本句柄的连接持有。
    ///
    /// 供**长任务**在关键节点（写库前、循环每一轮）调用。返回 false 说明连接已断、
    /// 锁已被服务端释放并可能被另一实例取走，此时必须停止写库并让位——
    /// 继续写就会出现两个实例同时处理同一批数据的局面。
    /// 短任务（毫秒级）不必调用，句柄的 Dispose 已经做了释放校验。
    /// </summary>
    public async Task<bool> IsHeldAsync()
    {
        if (_released) return false;
        return await _owner.StillHeldAsync(_conn, Name);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_released) return;
        _released = true;
        await _owner.ReleaseAsync(_conn, Name);
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

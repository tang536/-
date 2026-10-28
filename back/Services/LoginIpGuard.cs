using System.Collections.Concurrent;

namespace ERental.Services;

/// <summary>
/// 登录 IP 防护闸：按来源 IP 累计登录失败次数，达到阈值后封锁该 IP 一段时间。
///
/// 为什么封锁 IP 而不封锁账号（原口径「多次登录失败自动锁定账号」，现按门店实际改为封 IP）：
///   ① 账号锁是**可被利用的拒绝服务**——任何人只要知道账号名，连续输错 5 次密码，
///      就能把正常员工（含默认管理员）挡在门外 10 分钟，攻击者不需要任何凭据；
///   ② 封 IP 封的是「出问题的来源」：攻击者在自己的网络上撞库，被封的也是它自己，
///      门店内网照常登录；误伤范围从「一个账号」缩小到「一个来源」；
///   ③ 与 Program.cs 认证接口限流（每 IP 每分钟 10 次）叠加，构成
///      「限流挡频率 → 封 IP 挡持续撞库」两层防线，单靠任何一层都不够。
///
/// 存储口径：进程内内存字典。本系统定位门店单实例部署，登录封锁不需要跨实例共享
/// （会话登记表落 system_config 是为了多实例顶下线，目的不同）；进程重启会清空封锁记录，
/// 此时限流策略仍然生效，属于可接受的降级。换来的是**零 Schema 变更**——
/// 文档约定的 17 张表不因此加表，账号侧的 login_fail_count / lock_until 字段保留
/// （计数留痕、管理员重置密码时清零），但不再由登录逻辑自动写入锁定。
/// </summary>
public class LoginIpGuard
{
    /// <summary>单 IP 的失败状态：累计次数 / 封锁截止 / 最近失败时间（清理过期项用）</summary>
    private readonly record struct FailState(int Count, DateTime? LockUntil, DateTime LastFailAt);

    private readonly ConcurrentDictionary<string, FailState> _states = new();

    /// <summary>登录是低频操作，一把锁足够，也远比「每 IP 一把锁」好推理正确性。
    /// 用 System.Threading.Lock（.NET 9+）：编译器可校验锁语义，避免 object 锁的误用</summary>
    private readonly Lock _gate = new();

    /// <summary>容量上限：只装「近期失败过或正在封锁」的 IP，超容时清理过期项，防止内存无限增长</summary>
    private const int Capacity = 4096;

    /// <summary>该 IP 当前是否处于封锁期</summary>
    public bool IsLocked(string ip, out DateTime until)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(ip, out var s) && s.LockUntil is { } u && u > DateTime.Now)
            {
                until = u;
                return true;
            }
            until = default;
            return false;
        }
    }

    /// <summary>
    /// 登记一次登录失败。返回是否刚好达到阈值、触发封锁。
    /// 封锁到期后的首次失败重新计数，不与旧账叠加。
    /// </summary>
    public bool RegisterFailure(string ip, int maxFail, int lockMinutes, out DateTime lockUntil)
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            var prev = _states.TryGetValue(ip, out var cur) ? cur : new FailState(0, null, now);
            var expired = prev.LockUntil is { } old && old <= now;
            var count = expired ? 1 : prev.Count + 1;
            var until = count >= maxFail ? now.AddMinutes(lockMinutes) : (DateTime?)null;
            _states[ip] = new FailState(count, until, now);
            TrimIfNeeded(now);
            if (until is { } u)
            {
                lockUntil = u;
                return true;
            }
            lockUntil = default;
            return false;
        }
    }

    /// <summary>该 IP 距离触发封锁还剩几次机会（用于错误提示，让用户知道边界在哪）</summary>
    public int Remaining(string ip, int maxFail)
    {
        lock (_gate)
        {
            var count = _states.TryGetValue(ip, out var s) ? s.Count : 0;
            return Math.Max(0, maxFail - count);
        }
    }

    /// <summary>登录成功：清掉该 IP 的失败记录（员工重启路由器换 IP 后同样从零开始）</summary>
    public void Reset(string ip)
    {
        lock (_gate) { _states.TryRemove(ip, out _); }
    }

    /// <summary>超容清理：只删「封锁已过期」或「一小时前失败过且未达阈值」的记录</summary>
    private void TrimIfNeeded(DateTime now)
    {
        if (_states.Count < Capacity) return;
        foreach (var kv in _states)
        {
            var stale = kv.Value.LockUntil is { } u
                ? u <= now
                : kv.Value.LastFailAt < now.AddHours(-1);
            if (stale) _states.TryRemove(kv.Key, out _);
        }
    }
}

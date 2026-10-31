using System.Collections.Concurrent;

namespace ERental.Services;

/// <summary>登录 IP 防护闸：按来源 IP 累计登录失败次数，达到阈值后封锁该 IP 一段时间</summary>
public class LoginIpGuard
{
    /// <summary>单 IP 的失败状态：累计次数 / 封锁截止 / 最近失败时间（清理过期项用）</summary>
    private readonly record struct FailState(int Count, DateTime? LockUntil, DateTime LastFailAt);

    private readonly ConcurrentDictionary<string, FailState> _states = new();

    /// <summary>登录是低频操作，一把锁足够，也远比「每 IP 一把锁」好推理正确性</summary>
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

    /// <summary>登记一次登录失败</summary>
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

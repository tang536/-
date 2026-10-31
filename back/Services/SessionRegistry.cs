using System.Text.Json;
using ERental.Data;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>在线会话登记表 —— 单点登录（同一账号不允许同时登录）</summary>
public sealed class SessionRegistry(AppDbContext db, ILogger<SessionRegistry> logger)
{
    /// <summary>会话键前缀</summary>
    public const string Prefix = "sess.";

    private readonly AppDbContext _db = db;
    private readonly ILogger<SessionRegistry> _logger = logger;

    /// <summary>登记表里的一行：当前会话标识</summary>
    private sealed record Payload(string Token, string? Revoked = null);

    /// <summary>账号唯一键：员工与顾客的 ID 各自独立计数，必须带类型前缀区分</summary>
    private static string KeyOf(string userType, int userId) => $"{Prefix}{userType}.{userId}";

    /// <summary>签发会话：返回新的会话标识，并顶掉同一账号此前的会话</summary>
    public async Task<string> IssueAsync(string userType, int userId)
    {
        var token = Guid.NewGuid().ToString("N");
        await SaveAsync(KeyOf(userType, userId), new Payload(token));
        return token;
    }

    /// <summary>该会话是否仍是该账号的当前有效会话（sid 缺失、已退出、已被顶掉都返回 false）</summary>
    public async Task<bool> IsCurrentAsync(string userType, int userId, string? sessionToken)
    {
        if (string.IsNullOrEmpty(sessionToken)) return false;
        var entry = await LoadAsync(KeyOf(userType, userId));
        return entry != null && string.Equals(entry.Token, sessionToken, StringComparison.Ordinal);
    }

    /// <summary>该账号的会话若被强制撤销过，返回撤销原因</summary>
    public async Task<string?> RevokedReasonOfAsync(string userType, int userId)
        => (await LoadAsync(KeyOf(userType, userId)))?.Revoked;

    /// <summary>一次取出「是否当前会话 + 撤销原因」，供中间件与续期校验使用： 两件事查同一行，合成一次查询，避免每个已认证请求打…</summary>
    public async Task<(bool Current, string? RevokedReason)> CheckAsync(
        string userType, int userId, string? sessionToken)
    {
        var entry = await LoadAsync(KeyOf(userType, userId));
        if (entry == null) return (false, null);
        var current = !string.IsNullOrEmpty(sessionToken)
                      && string.Equals(entry.Token, sessionToken, StringComparison.Ordinal);
        return (current, entry.Revoked);
    }

    /// <summary>退出登录：只撤销当前这一个会话</summary>
    public async Task RevokeAsync(string userType, int userId, string? sessionToken)
    {
        if (string.IsNullOrEmpty(sessionToken)) return;
        var key = KeyOf(userType, userId);
        var entry = await LoadAsync(key);
        if (entry == null) return;
        if (!string.Equals(entry.Token, sessionToken, StringComparison.Ordinal)) return;
        // 主动登出不做墓碑——该 token 已被客户端丢弃，无需再解释原因。
        // 条件删除：中途被 IssueAsync 换掉则删不掉，不会误伤新会话。
        await _db.SystemConfigs
            .Where(c => c.ConfigKey == key && c.ConfigValue == Serialize(entry))
            .ExecuteDeleteAsync();
    }

    /// <summary>强制该账号全部会话下线，并留下原因供中间件提示</summary>
    public async Task RevokeAllAsync(string userType, int userId, string reason)
        => await SaveAsync(KeyOf(userType, userId), new Payload(string.Empty, reason));

    // ── 存取 ──

    private async Task<Payload?> LoadAsync(string key)
    {
        // 单点登录校验是**每个已认证请求**都会走的读路径，且位于授权中间件里、没有任何
        // try/catch 兜底——一旦命中死连接就会 500，表现为「闲置后再操作首次报错、重试才好」。
        // 对连接层瞬时故障整段重试（拿到新连接即可恢复），业务异常不重试。
        var value = await DbConcurrency.RetryTransientAsync(async () =>
        {
            return await _db.SystemConfigs.AsNoTracking()
                .Where(c => c.ConfigKey == key)
                .Select(c => c.ConfigValue)
                .FirstOrDefaultAsync();
        }, _logger, "会话校验");
        return Deserialize(value);
    }

    /// <summary>写入（存在则改、不存在则插）</summary>
    private async Task SaveAsync(string key, Payload payload)
    {
        var json = Serialize(payload);
        var row = await _db.SystemConfigs.FirstOrDefaultAsync(c => c.ConfigKey == key);
        if (row == null)
        {
            _db.SystemConfigs.Add(SystemConfig.Create(
                key, json, "在线会话（系统维护，请勿手工修改）"));
            try
            {
                await _db.SaveChangesAsync();
                return;
            }
            catch (DbUpdateException)
            {
                // 并发下已被另一实例插入：脱离跟踪，改走更新分支
                _db.Entry(_db.SystemConfigs.Local.First(c => c.ConfigKey == key)).State = EntityState.Detached;
            }
        }

        await _db.SystemConfigs
            .Where(c => c.ConfigKey == key)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ConfigValue, json)
                .SetProperty(c => c.UpdateTime, DateTime.Now));
    }

    private static string Serialize(Payload p) => JsonSerializer.Serialize(p);

    private static Payload? Deserialize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonSerializer.Deserialize<Payload>(raw); }
        catch (JsonException) { return null; }   // 被手工改坏的键按"无会话"处理，登录一次即自愈
    }
}

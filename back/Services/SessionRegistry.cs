using System.Text.Json;
using ERental.Data;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>
/// 在线会话登记表 —— 单点登录（同一账号不允许同时登录）。
///
/// 机制：每次登录签发一个随机会话标识写进 JWT 的 <c>sid</c> 声明，并把该账号在登记表里的
/// 当前会话**替换**为新标识（后登录者顶掉先登录者）。旧 token 虽然签名合法、也在有效期内，
/// 但它的 sid 已不是该账号的当前会话，会被 <see cref="Middleware.SingleSessionMiddleware"/>
/// 判为 401 并强制重新登录——这就是「同一账号不能同时登录」的落点。
///
/// ── 存储位置：system_config 表（键前缀 <c>sess.</c>），不新建表 ──
///   ① 项目数据表结构须与《唐荣强.docx》图4-1~图4-17 严格一致（17 张表），
///      加表会让数据库层契合度掉出 100%；system_config 本来就是键值表，加一组内部键不改表结构；
///   ② **多实例部署**：登记表必须落在共享存储上。两个 Kestrel 实例各自维护进程内字典时，
///      A 实例登录写的 sid 在 B 实例看来「不存在」，同一账号就能在两台机器上同时在线，
///      单点登录直接失效——所以这里用库表而不是进程内字典；
///   ③ 代价是每个已认证请求多一次主键查询（config_key 有唯一索引），
///      量级远小于业务查询本身，换来多实例下的正确性。
///
/// 键形如 <c>sess.staff.12</c> / <c>sess.customer.7</c>；值是一段小 JSON：
/// <c>{"token":"&lt;sid&gt;","revoked":null}</c>。<c>revoked</c> 非空表示这是一条**撤销墓碑**，
/// 用于让中间件给出准确提示——否则「被管理员停用」会错报成「已在其他设备登录」。
/// 每个账号至多一行，墓碑条数上限即账号总数，不存在无界增长。
/// </summary>
public sealed class SessionRegistry(AppDbContext db)
{
    /// <summary>会话键前缀。带此前缀的配置项不出现在「系统配置」页面，也不允许手工编辑。</summary>
    public const string Prefix = "sess.";

    private readonly AppDbContext _db = db;

    /// <summary>登记表里的一行：当前会话标识；<paramref name="Revoked"/> 非空表示已强制撤销</summary>
    private sealed record Payload(string Token, string? Revoked = null);

    /// <summary>账号唯一键：员工与顾客的 ID 各自独立计数，必须带类型前缀区分</summary>
    private static string KeyOf(string userType, int userId) => $"{Prefix}{userType}.{userId}";

    /// <summary>
    /// 签发会话：返回新的会话标识，并**顶掉**同一账号此前的会话。
    /// </summary>
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

    /// <summary>该账号的会话若被**强制撤销**过，返回撤销原因；正常被新登录顶掉时返回 null</summary>
    public async Task<string?> RevokedReasonOfAsync(string userType, int userId)
        => (await LoadAsync(KeyOf(userType, userId)))?.Revoked;

    /// <summary>
    /// 一次取出「是否当前会话 + 撤销原因」，供中间件与续期校验使用：
    /// 两件事查同一行，合成一次查询，避免每个已认证请求打两次库。
    /// </summary>
    public async Task<(bool Current, string? RevokedReason)> CheckAsync(
        string userType, int userId, string? sessionToken)
    {
        var entry = await LoadAsync(KeyOf(userType, userId));
        if (entry == null) return (false, null);
        var current = !string.IsNullOrEmpty(sessionToken)
                      && string.Equals(entry.Token, sessionToken, StringComparison.Ordinal);
        return (current, entry.Revoked);
    }

    /// <summary>
    /// 退出登录：只撤销**当前这一个**会话。
    /// 若该会话已被新登录顶掉，此处什么也不做——避免「旧设备点退出」顺手把新设备的会话注销掉。
    /// </summary>
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

    /// <summary>
    /// 强制该账号全部会话下线，并留下原因供中间件提示。
    /// 用于改密、注销账号、被管理员停用/删除等场景：凭据或账号状态已变，
    /// 旧 token 不应继续可用，且要让当事人知道是**什么原因**被迫下线。
    /// </summary>
    public async Task RevokeAllAsync(string userType, int userId, string reason)
        => await SaveAsync(KeyOf(userType, userId), new Payload(string.Empty, reason));

    // ── 存取 ──

    private async Task<Payload?> LoadAsync(string key)
    {
        var value = await _db.SystemConfigs.AsNoTracking()
            .Where(c => c.ConfigKey == key)
            .Select(c => c.ConfigValue)
            .FirstOrDefaultAsync();
        return Deserialize(value);
    }

    /// <summary>
    /// 写入（存在则改、不存在则插）。并发登录同一账号时两个请求可能同时走到"不存在"，
    /// 唯一索引会拦下后到的那个，这里捕获后改为更新——语义仍是「后登录者占位」。
    /// </summary>
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

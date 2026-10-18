using System.Collections.Concurrent;

namespace ERental.Services;

/// <summary>
/// 在线会话登记表 —— 单点登录（同一账号不允许同时登录）。
///
/// 机制：每次登录签发一个随机会话标识写进 JWT 的 <c>sid</c> 声明，并把该账号在登记表里的
/// 当前会话**替换**为新标识（后登录者顶掉先登录者）。旧 token 虽然签名合法、也在有效期内，
/// 但它的 sid 已不是该账号的当前会话，会被 <see cref="Middleware.SingleSessionMiddleware"/>
/// 判为 401 并强制重新登录——这就是「同一账号不能同时登录」的落点。
///
/// 为什么用进程内字典，而不是给 system_user / customer 加一列或新建会话表：
///   ① 本项目数据表结构须与《唐荣强.docx》图4-1~图4-17 严格一致（17 张表 / 134 字段），
///      加列或加表会让数据库层契合度掉出 100%；
///   ② 系统按需求 7.3 / 7.4 为门店**本地单实例部署**（Kestrel 单进程），不存在多节点共享会话的问题；
///   ③ 进程重启后登记表清空 = 全部登录态失效，语义比「重启后旧 token 仍能顶掉新登录」更干净。
/// 若将来要做多节点部署，把本类换成 Redis 实现即可，调用方（AuthService / 中间件）无需改动。
/// </summary>
public sealed class SessionRegistry
{
    /// <summary>
    /// 一个在线会话：标识 + 签发时刻。
    /// <paramref name="Revoked"/> 非空表示这是一条**撤销墓碑**（会话已失效，仅保留原因），
    /// 用于让中间件给出准确提示——否则"被管理员停用"会错报成"已在其他设备登录"。
    /// 墓碑条数上限即账号总数，不存在无界增长。
    /// </summary>
    private sealed record Entry(string Token, string? Revoked = null);

    private readonly ConcurrentDictionary<string, Entry> _sessions = new();

    /// <summary>账号唯一键：员工与顾客的 ID 各自独立计数，必须带类型前缀区分</summary>
    private static string KeyOf(string userType, int userId) => $"{userType}:{userId}";

    /// <summary>
    /// 签发会话：返回新的会话标识，并**顶掉**同一账号此前的会话。
    /// </summary>
    public string Issue(string userType, int userId)
    {
        var token = Guid.NewGuid().ToString("N");
        _sessions[KeyOf(userType, userId)] = new Entry(token);
        return token;
    }

    /// <summary>该会话是否仍是该账号的当前有效会话（sid 缺失、已退出、已被顶掉都返回 false）</summary>
    public bool IsCurrent(string userType, int userId, string? sessionToken)
    {
        if (string.IsNullOrEmpty(sessionToken)) return false;
        return _sessions.TryGetValue(KeyOf(userType, userId), out var entry)
               && string.Equals(entry.Token, sessionToken, StringComparison.Ordinal);
    }

    /// <summary>该账号的会话若被**强制撤销**过，返回撤销原因；正常被新登录顶掉时返回 null</summary>
    public string? RevokedReasonOf(string userType, int userId) =>
        _sessions.TryGetValue(KeyOf(userType, userId), out var entry) ? entry.Revoked : null;

    /// <summary>
    /// 退出登录：只撤销**当前这一个**会话。
    /// 若该会话已被新登录顶掉，此处什么也不做——避免「旧设备点退出」顺手把新设备的会话注销掉。
    /// </summary>
    public void Revoke(string userType, int userId, string? sessionToken)
    {
        if (string.IsNullOrEmpty(sessionToken)) return;
        var key = KeyOf(userType, userId);
        if (!_sessions.TryGetValue(key, out var current)) return;
        if (!string.Equals(current.Token, sessionToken, StringComparison.Ordinal)) return;
        // 配对删除（值语义）：中途被 Issue 换掉则删不掉，不会误伤新会话。
        // 主动登出不做墓碑——该 token 已被客户端丢弃，无需再解释原因。
        _sessions.TryRemove(new KeyValuePair<string, Entry>(key, current));
    }

    /// <summary>
    /// 强制该账号全部会话下线，并留下原因供中间件提示。
    /// 用于改密、注销账号、被管理员停用/删除等场景：凭据或账号状态已变，
    /// 旧 token 不应继续可用，且要让当事人知道是**什么原因**被迫下线。
    /// </summary>
    public void RevokeAll(string userType, int userId, string reason) =>
        _sessions[KeyOf(userType, userId)] = new Entry(string.Empty, reason);
}

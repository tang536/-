using ERental.Data;
using ERental.Models;
using ERental.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERental.Services;

/// <summary>
/// ComfyUI 服务地址的动态配置（键 <c>ai.comfyui_base_url</c>，存 system_config 表）。
///
/// 为什么不能把地址写死在 appsettings 里：ComfyUI 桌面版会换端口、换机器、
/// 换 IP（重装、升级、局域网内挪到另一台电脑跑）——地址一改就得改配置文件
/// 重启后端，门店里没人会改代码。管理员在「系统配置」页填一下即生效。
///
/// 取值优先级：system_config 里的值 &gt; appsettings 的 <c>AiImage:BaseUrl</c>
/// （后者只当出厂默认兜底，正常情况下种子数据会把同一地址写进库里）。
/// </summary>
public class ComfyConfigService(AppDbContext db, IOptions<AiImageOptions> opt)
{
    private readonly AppDbContext _db = db;

    private readonly AiImageOptions _opt = opt.Value;

    /// <summary>配置键。带此前缀的项归本服务管理，不出现在普通系统配置列表。</summary>
    private const string Key = "ai.comfyui_base_url";

    private string? _cache;
    private DateTime _cacheAt = DateTime.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>读库缓存时长。保存后立即 <see cref="Invalidate"/>，正常情况下改动 3 秒内必生效。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);


    /// <summary>出厂默认地址（appsettings），配置页用来展示「没配过时用这个」。</summary>
    public string DefaultUrl => Normalize(_opt.BaseUrl);

    /// <summary>
    /// 当前生效地址：库里配了用库里的，没配用 appsettings 默认值。
    /// 带 3 秒缓存，避免每次生成/查询都打库。
    /// </summary>
    public async Task<string> GetBaseUrlAsync(CancellationToken ct = default)
    {
        if (_cache != null && DateTime.UtcNow - _cacheAt < CacheTtl) return _cache;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache != null && DateTime.UtcNow - _cacheAt < CacheTtl) return _cache;
            var row = await _db.SystemConfigs.AsNoTracking()
                .FirstOrDefaultAsync(c => c.ConfigKey == Key, ct);
            var value = row?.ConfigValue;
            _cache = Normalize(string.IsNullOrWhiteSpace(value) ? _opt.BaseUrl : value);
            _cacheAt = DateTime.UtcNow;
            return _cache;
        }
        finally { _gate.Release(); }
    }

    /// <summary>生效值 + 来源（db / appsettings），配置页展示「这个地址现在由谁提供」。</summary>
    public async Task<(string Url, string Source)> GetEffectiveAsync(CancellationToken ct = default)
    {
        var row = await _db.SystemConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConfigKey == Key, ct);
        var value = (row?.ConfigValue ?? "").Trim();
        return value.Length == 0 ? (Normalize(_opt.BaseUrl), "appsettings") : (Normalize(value), "db");
    }

    /// <summary>
    /// 保存地址。校验：http/https 绝对地址、无空白字符、≤200 字符
    /// （system_config.config_value 列宽 200，超长会被数据库直接截断，必须提前拦）。
    /// </summary>
    public async Task<string> SetAsync(string url, CancellationToken ct = default)
    {
        var text = url.Trim();
        if (text.Length == 0) throw new Exception("ComfyUI 地址不能为空");
        if (text.Length > 200) throw new Exception("地址过长（最多 200 字符）");
        if (text.Any(char.IsWhiteSpace)) throw new Exception("地址不能包含空格或换行");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new Exception("地址格式不正确：应为 http://主机:端口（如 http://127.0.0.1:8188）");

        var normalized = Normalize(text);
        var row = await _db.SystemConfigs.FirstOrDefaultAsync(c => c.ConfigKey == Key, ct);
        if (row == null)
        {
            _db.SystemConfigs.Add(SystemConfig.Create(
                Key, normalized, "AI 生图-ComfyUI 服务地址（桌面版默认 127.0.0.1:8188）"));
        }
        else
        {
            row.SetValue(normalized);
        }
        await _db.SaveChangesAsync(ct);
        Invalidate();
        return normalized;
    }

    /// <summary>清缓存（保存后调用，让新地址对后续生图立即生效）。</summary>
    private void Invalidate()
    {
        _cache = null;
        _cacheAt = DateTime.MinValue;
    }

    /// <summary>去掉末尾斜杠，保证拼接相对路径时不出现双斜杠。</summary>
    private static string Normalize(string url)
    {
        var u = url.Trim();
        while (u.EndsWith('/')) u = u[..^1];
        return u.Length == 0 ? "http://127.0.0.1:8188" : u;
    }
}

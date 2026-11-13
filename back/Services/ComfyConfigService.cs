using ERental.Data;
using ERental.Models;
using ERental.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERental.Services;

///<summary>ComfyUI 服务地址的动态配置</summary>
public class ComfyConfigService(AppDbContext db, IOptions<AiImageOptions> opt) : IAsyncDisposable
{
    private readonly AppDbContext _db = db;

    private readonly AiImageOptions _opt = opt.Value;

    ///<summary>配置键</summary>
    private const string Key = "ai.comfyui_base_url";

    private string? _cache;
    private DateTime _cacheAt = DateTime.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);

    ///<summary>读库缓存时长</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);

    ///<summary>出厂默认地址（appsettings），配置页用来展示「没配过时用这个」</summary>
    public string DefaultUrl => Normalize(_opt.BaseUrl);

    ///<summary>当前生效地址：库里配了用库里的，没配用 appsettings 默认值</summary>
    public async Task<string> GetBaseUrlAsync(CancellationToken ct = default)
    {
        if (_cache != null && AppClock.UtcNow - _cacheAt < CacheTtl) return _cache;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache != null && AppClock.UtcNow - _cacheAt < CacheTtl) return _cache;
            var row = await _db.SystemConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.ConfigKey == Key, ct);
            var value = row?.ConfigValue;
            _cache = Normalize(string.IsNullOrWhiteSpace(value) ? _opt.BaseUrl : value);
            _cacheAt = AppClock.UtcNow;
            return _cache;
        }
        finally { _gate.Release(); }
    }

    ///<summary>生效值+来源（db/appsettings），配置页展示「这个地址现在由谁提供」</summary>
    public async Task<(string Url, string Source)> GetEffectiveAsync(CancellationToken ct = default)
    {
        var row = await _db.SystemConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.ConfigKey == Key, ct);
        var value = (row?.ConfigValue ?? "").Trim();
        return value.Length == 0 ? (Normalize(_opt.BaseUrl), "appsettings") : (Normalize(value), "db");
    }

    ///<summary>保存地址</summary>
    public async Task<string> SetAsync(string url, CancellationToken ct = default)
    {
        var text = url.Trim();
        if (text.Length == 0) throw new BusinessException("ComfyUI 地址不能为空");
        if (text.Length > 200) throw new BusinessException("地址过长（最多 200 字符）");
        if (text.Any(char.IsWhiteSpace)) throw new BusinessException("地址不能包含空格或换行");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) throw new BusinessException("地址格式不正确：应为 http://主机:端口（如 http://127.0.0.1:8188）");

        var normalized = Normalize(text);
        var row = await _db.SystemConfigs.FirstOrDefaultAsync(c => c.ConfigKey == Key, ct);
        if (row == null)
        {
            _db.SystemConfigs.Add(SystemConfig.Create(Key, normalized, "AI 生图-ComfyUI 服务地址（桌面版默认 127.0.0.1:8188）"));
        }
        else
        {
            row.SetValue(normalized);
        }
        await _db.SaveChangesAsync(ct);
        await InvalidateAsync(ct);
        return normalized;
    }

    ///<summary>清缓存（保存后调用，让新地址对后续生图立即生效）</summary>
    private async Task InvalidateAsync(CancellationToken ct = default)
    {
        //与 GetBaseUrlAsync 共用一把锁
        await _gate.WaitAsync(ct);
        try
        {
            _cache = null;
            _cacheAt = DateTime.MinValue;
        }
        finally { _gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    ///<summary>去掉末尾斜杠，保证拼接相对路径时不出现双斜杠</summary>
    private static string Normalize(string url)
    {
        var u = url.Trim();
        while (u.EndsWith('/')) u = u[..^1];
        return u.Length == 0 ? "http://127.0.0.1:8188" : u;
    }
}

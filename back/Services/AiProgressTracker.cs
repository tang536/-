using System.Collections.Concurrent;

namespace ERental.Services;

/// <summary>
/// 一次生成任务的进度快照。前端提交生成后凭 jobId 轮询
/// <c>GET /api/ai-image/progress/{jobId}</c> 渲染进度条，直到 State 变为
/// done（Message = 图片地址）或 error（Message = 失败原因）。
/// </summary>
public class AiProgressState
{
    /// <summary>running / done / error。</summary>
    public string State { get; set; } = "running";

    /// <summary>0~99；100 只由 <see cref="AiProgressTracker.Complete"/> 打上，
    /// 避免「采样跑完」被误显示成「全部完成」（后面还有 VAE 解码与落盘）。</summary>
    public int Percent { get; set; }

    /// <summary>阶段中文说明：排队中 / 执行节点 12 / 采样中 5/20 步 / 生成完成。</summary>
    public string Stage { get; set; } = string.Empty;

    /// <summary>done = 成品图 URL；error = 失败原因。</summary>
    public string? Message { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 生成进度跟踪器（进程内单例）。
/// ComfyUI 的 WebSocket 消息由 <see cref="ComfyUiClient"/> 收上来，经
/// <see cref="AiImageService"/> 写进这里；前端轮询读取。
/// 条目保留 30 分钟自动清扫（查询时顺带扫），防止长跑内存无限增长。
/// </summary>
public class AiProgressTracker
{
    private readonly ConcurrentDictionary<string, AiProgressState> _map = new();

    /// <summary>登记一个任务（提交时调用，保证轮询立刻有值可取）。</summary>
    public void Begin(string jobId) => _map.GetOrAdd(jobId, _ => new AiProgressState());

    /// <summary>
    /// 更新进度百分比与阶段说明。
    /// Percent 取「历史最大」：进度条只许前进不许回退——采样前的「执行节点」
    /// 阶段给的是粗估值，采样开始时的真实百分比可能更低，取 max 避免UI倒退。
    /// 上限 99：100 只由 <see cref="Complete"/> 打上（采样完还有 VAE 解码与落盘）。
    /// </summary>
    public void Report(string jobId, int percent, string stage)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        var s = _map.GetOrAdd(jobId, _ => new AiProgressState());
        s.Percent = Math.Max(s.Percent, Math.Clamp(percent, 0, 99));
        s.Stage = stage;
        s.UpdatedAt = DateTime.Now;
    }

    /// <summary>完成：100% + 成品图地址。</summary>
    public void Complete(string jobId, string url)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        var s = _map.GetOrAdd(jobId, _ => new AiProgressState());
        s.State = "done";
        s.Percent = 100;
        s.Stage = "生成完成";
        s.Message = url;
        s.UpdatedAt = DateTime.Now;
    }

    /// <summary>失败：记录原因，前端轮询到即停止等待。</summary>
    public void Fail(string jobId, string message)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        var s = _map.GetOrAdd(jobId, _ => new AiProgressState());
        s.State = "error";
        s.Stage = "生成失败";
        s.Message = message;
        s.UpdatedAt = DateTime.Now;
    }

    /// <summary>查进度；命中时顺手做过期清扫，未命中时全量扫一次（控制成本）。</summary>
    public AiProgressState? Get(string jobId)
    {
        if (string.IsNullOrEmpty(jobId)) return null;
        if (_map.TryGetValue(jobId, out var s))
        {
            if (DateTime.Now - s.UpdatedAt > TimeSpan.FromMinutes(30))
            {
                _map.TryRemove(jobId, out _);
                return null;
            }
            return s;
        }
        foreach (var kv in _map)
            if (DateTime.Now - kv.Value.UpdatedAt > TimeSpan.FromMinutes(30))
                _map.TryRemove(kv.Key, out _);
        return null;
    }
}

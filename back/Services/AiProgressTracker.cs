using System.Collections.Concurrent;

namespace ERental.Services;

/// <summary>一次生成任务的进度快照</summary>
public class AiProgressState
{
    /// <summary>running / done / error</summary>
    public string State { get; set; } = "running";

    /// <summary>0~99</summary>
    public int Percent { get; set; }

    /// <summary>阶段中文说明：排队中 / 执行节点 12 / 采样中 5/20 步 / 生成完成</summary>
    public string Stage { get; set; } = string.Empty;

    /// <summary>done = 成品图 URL</summary>
    public string? Message { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>生成进度跟踪器（进程内单例）</summary>
public class AiProgressTracker
{
    private readonly ConcurrentDictionary<string, AiProgressState> _map = new();

    /// <summary>登记一个任务（提交时调用，保证轮询立刻有值可取）</summary>
    public void Begin(string jobId) => _map.GetOrAdd(jobId, _ => new AiProgressState());

    /// <summary>更新进度百分比与阶段说明</summary>
    public void Report(string jobId, int percent, string stage)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        var s = _map.GetOrAdd(jobId, _ => new AiProgressState());
        s.Percent = Math.Max(s.Percent, Math.Clamp(percent, 0, 99));
        s.Stage = stage;
        s.UpdatedAt = DateTime.Now;
    }

    /// <summary>完成：100% + 成品图地址</summary>
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

    /// <summary>失败：记录原因，前端轮询到即停止等待</summary>
    public void Fail(string jobId, string message)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        var s = _map.GetOrAdd(jobId, _ => new AiProgressState());
        s.State = "error";
        s.Stage = "生成失败";
        s.Message = message;
        s.UpdatedAt = DateTime.Now;
    }

    /// <summary>查进度；命中时顺手做过期清扫，未命中时全量扫一次（控制成本）</summary>
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

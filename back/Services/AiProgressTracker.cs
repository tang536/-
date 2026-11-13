using System.Collections.Concurrent;
using ERental.Data;

namespace ERental.Services;

///<summary>一次生成任务的进度快照</summary>
public class AiProgressState
{
    ///<summary>running/done/error</summary>
    public string State { get; set; } = "running";

    ///<summary>0~99</summary>
    public int Percent { get; set; }

    ///<summary>阶段中文说明：排队中/执行节点 12/采样中 5/20 步/生成完成</summary>
    public string Stage { get; set; } = string.Empty;

    ///<summary>done=成品图 URL</summary>
    public string? Message { get; set; }

    public DateTime UpdatedAt { get; set; } = AppClock.UtcNow;
}

///<summary>生成进度跟踪器（进程内单例）</summary>
public class AiProgressTracker
{
    private readonly ConcurrentDictionary<string, AiProgressState> _map = new();

    ///<summary>进度表容量上限</summary>
    private const int MaxEntries = 512;

    ///<summary>取（或建）一条状态，并在每次写入时顺带做兜底容量驱逐</summary>
    private AiProgressState Entry(string jobId)
    {
        //注意别再递归调自己：这里必须真正落到 _map.GetOrAdd
        var s = _map.GetOrAdd(jobId, _ => new AiProgressState { UpdatedAt = AppClock.UtcNow });
        EvictIfNeeded();
        return s;
    }

    ///<summary>超过上限时按 UpdatedAt 淘汰最旧的一批（兜底，独立于 Get 里的过期清扫）</summary>
    private void EvictIfNeeded()
    {
        if (_map.Count <= MaxEntries) return;
        //多淘汰一批
        var excess = _map.Count - MaxEntries + 32;
        foreach (var kv in _map.OrderBy(p => p.Value.UpdatedAt).Take(excess))
            _map.TryRemove(kv.Key, out _);
    }

    ///<summary>登记一个任务（提交时调用，保证轮询立刻有值可取）</summary>
    public void Begin(string jobId) { Entry(jobId); }

    ///<summary>更新进度百分比与阶段说明</summary>
    public void Report(string jobId, int percent, string stage)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        var s = Entry(jobId);
        s.Percent = Math.Max(s.Percent, Math.Clamp(percent, 0, 99));
        s.Stage = stage;
        s.UpdatedAt = AppClock.UtcNow;
    }

    ///<summary>完成：100% + 成品图地址</summary>
    public void Complete(string jobId, string url)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        var s = Entry(jobId);
        s.State = "done";
        s.Percent = 100;
        s.Stage = "生成完成";
        s.Message = url;
        s.UpdatedAt = AppClock.UtcNow;
    }

    ///<summary>失败：记录原因，前端轮询到即停止等待</summary>
    public void Fail(string jobId, string message)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        var s = Entry(jobId);
        s.State = "error";
        s.Stage = "生成失败";
        s.Message = message;
        s.UpdatedAt = AppClock.UtcNow;
    }

    ///<summary>查进度</summary>
    public AiProgressState? Get(string jobId)
    {
        if (string.IsNullOrEmpty(jobId)) return null;
        if (_map.TryGetValue(jobId, out var s))
        {
            if (AppClock.UtcNow - s.UpdatedAt > TimeSpan.FromMinutes(30))
            {
                _map.TryRemove(jobId, out _);
                return null;
            }
            return s;
        }
        foreach (var kv in _map)
            if (AppClock.UtcNow - kv.Value.UpdatedAt > TimeSpan.FromMinutes(30)) _map.TryRemove(kv.Key, out _);
        return null;
    }
}

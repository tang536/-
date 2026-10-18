using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ERental.Options;
using Microsoft.Extensions.Options;

namespace ERental.Services;

/// <summary>WS 推送的一次进度快照（Percent 0~99，100 留给完成态）。</summary>
public record ComfyProgress(int Percent, string Stage);

/// <summary>
/// ComfyUI HTTP API 的薄封装。只做「收发」：上传图片、提交工作流、收执行进度、
/// 查历史、下载成品。工作流 JSON 的改写（打补丁）在 <see cref="AiImageService"/>，
/// 两者刻意分开——客户端不关心图长什么样，服务不关心 HTTP 怎么发。
///
/// 接口清单（ComfyUI 官方 API，桌面版同样提供）：
///  POST /upload/image        上传参考图，返回服务端文件名
///  POST /prompt              提交工作流（prompt 即 API 格式的节点字典）
///  GET  /history/{prompt_id} 查看任务结果（含各节点输出图片）
///  GET  /view                按文件名下载输出图片
///  GET  /system_stats        探活
///  WS   /ws?clientId=xxx     实时进度（progress / executing / execution_* 消息）
///
/// 地址不写死：BaseUrl 由 <see cref="ComfyConfigService"/> 动态提供（系统配置页可改），
/// 每次请求按当时生效值拼绝对地址，管理员改完最多 3 秒即生效。
/// </summary>
public class ComfyUiClient
{
    private readonly HttpClient _http;
    private readonly AiImageOptions _opt;
    private readonly ComfyConfigService _cfg;
    // object_info 缓存：同一批节点类型会被 schema 生成与提交前预检反复查询，
    // ComfyUI 每次全量返回该节点定义（模型多的时候下拉列表很长），缓存降载。
    private readonly Dictionary<string, JsonElement?> _infoCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _infoGate = new(1, 1);

    public ComfyUiClient(HttpClient http, IOptions<AiImageOptions> opt, ComfyConfigService cfg)
    {
        _http = http;
        _opt = opt.Value;
        _cfg = cfg;
        // BaseAddress 不在这里设：地址可在系统配置页动态修改，每次请求按生效值拼绝对 URI。
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(30, _opt.TimeoutSeconds));
    }

    /// <summary>当前生效的 ComfyUI 地址（配置页回显 / 离线提示用）。</summary>
    public Task<string> EffectiveBaseUrlAsync(CancellationToken ct = default) => _cfg.GetBaseUrlAsync(ct);

    /// <summary>探活：桌面版没启动时这里会抛异常，由上层翻译成「ComfyUI 未启动」。</summary>
    public async Task<bool> IsOnlineAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{await _cfg.GetBaseUrlAsync(ct)}/system_stats", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>上传参考图（图生图用），返回 ComfyUI 侧的文件名。</summary>
    public async Task<string> UploadImageAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "image", fileName);
        // overwrite=true：同名参考图直接覆盖，避免 input 目录堆积
        form.Add(new StringContent("true"), "overwrite");

        var resp = await _http.PostAsync($"{await _cfg.GetBaseUrlAsync(ct)}/upload/image", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"ComfyUI 上传参考图失败（HTTP {(int)resp.StatusCode}）：{Trim(body)}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("name").GetString()
            ?? throw new Exception("ComfyUI 上传响应里没有文件名");
    }

    /// <summary>
    /// 提交工作流，返回 prompt_id（后续凭它查历史 / 收 WS 进度）。
    /// <paramref name="clientId"/> 即 WS 连接用的 clientId——ComfyUI 只把执行消息
    /// 推给携带同一 clientId 的 WebSocket 连接，两边必须一致。
    /// </summary>
    private async Task<string> SubmitPromptAsync(string promptJson, string? clientId = null, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            prompt = JsonNode.Parse(promptJson),
            client_id = string.IsNullOrEmpty(clientId) ? "erental" : clientId
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{await _cfg.GetBaseUrlAsync(ct)}/prompt", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception(TranslateSubmitError(body, (int)resp.StatusCode));

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("prompt_id").GetString()
            ?? throw new Exception("ComfyUI 提交响应里没有 prompt_id");
    }

    /// <summary>
    /// 提交并等待执行完成，返回 prompt_id。执行期间的实时进度经
    /// <paramref name="progress"/> 上报（前端进度条的数据来源）。
    ///
    /// 两条通道，自动择优：
    ///  ① WebSocket（首选）：连 /ws?clientId= 收 progress / executing / execution_*，
    ///     拿到「采样第几步、执行到哪个节点、失败原因」，进度细到每一步；
    ///  ② HTTP 轮询（兜底）：WS 连不上（极老版本 / 被中间设备挡）时退回轮询
    ///     /history/{id}，只有「执行中 → 完成」两态，进度条给粗估值。
    /// </summary>
    public async Task<string> ExecutePromptAsync(
        string promptJson, IProgress<ComfyProgress>? progress, TimeSpan timeout, CancellationToken ct = default)
    {
        // 超时统一在这里管：提交 + 执行 + 轮询一并计入，到点即抛中文超时
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var token = linked.Token;

        var baseUrl = await _cfg.GetBaseUrlAsync(token);
        // 每次生成一个 clientId：WS 只收本任务的消息，多客户端并行时不串台
        var clientId = Guid.NewGuid().ToString("N");

        // ① 尝试 WS 实时进度；连接失败或流中断 → ② HTTP 轮询兜底
        string promptId;
        bool wsCompleted;
        try
        {
            promptId = await SubmitPromptAsync(promptJson, clientId, token);
            progress?.Report(new ComfyProgress(1, "已提交"));
            wsCompleted = await ListenProgressAsync(baseUrl, clientId, promptId, progress, token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 触发源是 timeoutCts（调用方没取消）→ 翻译成中文超时；
            // 调用方真取消了则原样上抛，别把用户主动取消说成超时
            throw new Exception($"生成超时（{timeout.TotalSeconds:0}s 内未出图）。可换更小的尺寸/步数，或确认 ComfyUI 是否在正常执行。");
        }
        if (!wsCompleted)
        {
            progress?.Report(new ComfyProgress(5, "执行中（实时进度不可用，请耐心等待）"));
            await PollUntilDoneAsync(promptId, progress, token);
        }
        return promptId;
    }

    /// <summary>
    /// 连 WS 收本任务的执行消息。
    /// 返回 true = 已收到 execution_success（或 executing(null) 收尾）；
    /// 返回 false = WS 连不上 / 流中途断开，调用方应转 HTTP 轮询。
    /// </summary>
    private static async Task<bool> ListenProgressAsync(
        string baseUrl, string clientId, string promptId, IProgress<ComfyProgress>? progress, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // 注意路径必须是 /ws：漏掉路径时请求会打到 /，ComfyUI 的升级处理器
            // 只认 /ws，其余路径直接断连（.NET 侧表现为「Unable to connect」）
            var wsUri = $"{ToWebSocketUri(baseUrl)}/ws?clientId={clientId}";
            await ws.ConnectAsync(new Uri(wsUri), connectCts.Token);
        }
        catch
        {
            return false; // WS 不可用：老版本 ComfyUI 或网络策略挡了升级请求
        }

        var buffer = new byte[64 * 1024];
        while (true)
        {
            // 一帧可能分多段到达（大 JSON / 分片），先攒完整条再解析
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return false; // 流断了 → 轮询兜底
                if (result.MessageType == WebSocketMessageType.Text)
                    message.Write(buffer, 0, result.Count);
                // 二进制帧是预览图 / 音频，进度用不上，收下即弃
            }
            while (!result.EndOfMessage);

            if (message.Length == 0) continue;
            var text = Encoding.UTF8.GetString(message.ToArray());
            var outcome = HandleWsMessage(text, promptId, progress);
            if (outcome == WsOutcome.Done) return true;
            if (outcome == WsOutcome.StreamEnded) return false;
            if (outcome == WsOutcome.Failed) throw new Exception("生成失败"); // 占位，下面替换为具体报错
            // 其余（Continue / 未知）：本条只是中间帧，继续读下一条
        }
    }

    private static WsOutcome HandleWsMessage(string text, string promptId, IProgress<ComfyProgress>? progress)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch { return WsOutcome.Continue; } // 非 JSON 帧（极少见），忽略
        if (node is not JsonObject obj) return WsOutcome.Continue;

        var type = obj["type"]?.GetValue<string>();
        if (obj["data"] is not JsonObject data) return WsOutcome.Continue;

        // 多客户端场景：带了 prompt_id 且不是本任务的，直接跳过
        // （重连补发的 executing 不带 prompt_id，照样放行）
        var msgPid = data["prompt_id"]?.GetValue<string>();
        if (msgPid != null && msgPid != promptId) return WsOutcome.Continue;

        switch (type)
        {
            case "execution_start":
                progress?.Report(new ComfyProgress(2, "开始执行"));
                break;
            case "execution_cached":
                progress?.Report(new ComfyProgress(10, "命中缓存节点，跳过重复计算"));
                break;
            case "executing":
                var nodeId = data["node"]?.GetValue<string>();
                if (nodeId is null)
                {
                    // executing(null) = 所有节点跑完（其后还会有 execution_success）
                    progress?.Report(new ComfyProgress(95, "节点执行完毕，正在保存"));
                    return WsOutcome.Done;
                }
                progress?.Report(new ComfyProgress(15, $"执行节点 {nodeId}"));
                break;
            case "progress":
                // ComfyUI 的采样步进：value/max 是当前节点的步数
                var value = data["value"]?.GetValue<double>() ?? 0;
                var max = data["max"]?.GetValue<double>() ?? 0;
                if (max > 0)
                    progress?.Report(new ComfyProgress((int)Math.Round(value / max * 100), $"采样中 {(long)value}/{(long)max} 步"));
                break;
            case "execution_success":
                progress?.Report(new ComfyProgress(99, "生成完成，正在下载"));
                return WsOutcome.Done;
            case "execution_error":
                throw BuildExecutionError(data);
            case "execution_interrupted":
                throw new Exception("生成已被中断（ComfyUI 侧取消了本次执行）");
            case "status":
                var queueLeft = data["status"]?["exec_info"]?["queue_remaining"]?.GetValue<int>() ?? 0;
                if (queueLeft > 0)
                    progress?.Report(new ComfyProgress(1, $"排队中（前方还有 {queueLeft} 个任务）"));
                break;
        }
        return WsOutcome.Continue;
    }

    /// <summary>把 ComfyUI 的 execution_error 数据翻成中文异常（节点定位 + 异常原文）。</summary>
    private static Exception BuildExecutionError(JsonObject data)
    {
        var nodeId = data["node_id"]?.GetValue<string>() ?? "";
        var nodeType = data["node_type"]?.GetValue<string>() ?? "";
        var message = data["exception_message"]?.GetValue<string>() ?? "";
        var exType = data["exception_type"]?.GetValue<string>() ?? "";

        var line = "ComfyUI 执行失败";
        if (nodeId.Length > 0) line += $"（节点 {nodeId} {nodeType}）".TrimEnd();
        if (message.Length > 0) line += $"：{Trim(message)}";
        else if (exType.Length > 0) line += $"：{exType}";
        return new Exception(line);
    }

    /// <summary>WS 不可用时的兜底：轮询历史直到出图（到点由 CancellationToken 抛出）。</summary>
    private async Task PollUntilDoneAsync(string promptId, IProgress<ComfyProgress>? progress, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(Math.Max(200, _opt.PollIntervalMs), ct);
            var outputs = await GetOutputImagesAsync(promptId, ct);
            if (outputs.Count > 0)
            {
                progress?.Report(new ComfyProgress(99, "生成完成，正在下载"));
                return;
            }
        }
    }

    /// <summary>
    /// 把 ComfyUI 的 400 校验错误翻译成中文。拒收时响应体形如：
    /// {"error": {...}, "node_errors": {"28": {"errors": [{"type": "value_not_in_list",
    ///  "message": "...", "details": "unet_name: 'x.safetensors' not in list",
    ///  "extra_info": {"input_name": "unet_name"}}], "class_type": "UNETLoader"}}}
    /// 逐节点译成「节点 28（UNETLoader）的 unet_name 取值不存在」这样的可执行信息。
    /// </summary>
    private static string TranslateSubmitError(string body, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var lines = new List<string>();

            if (root.TryGetProperty("node_errors", out var nodeErrors) &&
                nodeErrors.ValueKind == JsonValueKind.Object)
            {
                foreach (var node in nodeErrors.EnumerateObject())
                {
                    var classType = node.Value.TryGetProperty("class_type", out var ctProp)
                        ? ctProp.GetString() ?? "" : "";
                    if (!node.Value.TryGetProperty("errors", out var errors)) continue;
                    foreach (var err in errors.EnumerateArray())
                    {
                        var type = err.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                        var details = err.TryGetProperty("details", out var d) ? d.GetString() ?? "" : "";
                        var field = err.TryGetProperty("extra_info", out var ei) &&
                                    ei.TryGetProperty("input_name", out var inm) ? inm.GetString() : null;

                        var line = $"节点 {node.Name}（{classType}）";
                        if (!string.IsNullOrEmpty(field)) line += $" 的 {field}";
                        line += type switch
                        {
                            "value_not_in_list" => " 取值不在可选列表（文件不存在或文件名不符）",
                            "required_input_missing" => " 缺少必填输入",
                            "invalid_type" => " 类型不正确",
                            "too_long" or "too_short" => " 长度超出限制",
                            "not_a_number" => " 不是有效数字",
                            _ => $" 校验失败（{type}）"
                        };
                        if (!string.IsNullOrEmpty(details)) line += $"：{Trim(details, 120)}";
                        lines.Add(line);
                    }
                }
            }

            if (lines.Count == 0 &&
                root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                var msg = error.TryGetProperty("message", out var em) ? em.GetString() ?? "" : "";
                var det = error.TryGetProperty("details", out var ed) ? ed.GetString() ?? "" : "";
                if (msg.Length > 0 || det.Length > 0)
                    lines.Add(msg + (det.Length > 0 ? $"：{Trim(det, 150)}" : ""));
            }

            if (lines.Count > 0)
                return $"ComfyUI 拒绝工作流（HTTP {status}）：{string.Join("；", lines)}";
        }
        catch { /* 响应不是 JSON，落回原始文本 */ }

        return $"ComfyUI 拒绝工作流（HTTP {status}）：{Trim(body)}";
    }

    /// <summary>
    /// 拉取某节点类型的输入定义（object_info），供提交前预检与表单 schema 生成用。
    /// 节点类型未安装时 ComfyUI 返回 404；老版本没有该端点时同样返回 null——
    /// 返回 null 一律表示「查不到，跳过这项检查」，不当作错误。
    /// 结果带进程内缓存（含 null 的负面缓存），避免同一模板被反复查询。
    /// </summary>
    public async Task<JsonElement?> GetNodeInfoAsync(string classType, CancellationToken ct = default)
    {
        if (_infoCache.TryGetValue(classType, out var cached)) return cached;
        await _infoGate.WaitAsync(ct);
        try
        {
            if (_infoCache.TryGetValue(classType, out cached)) return cached;
            var resp = await _http.GetAsync(
                $"{await _cfg.GetBaseUrlAsync(ct)}/object_info/{Uri.EscapeDataString(classType)}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _infoCache[classType] = null;
                return null;
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty(classType, out var spec))
            {
                _infoCache[classType] = null;
                return null;
            }
            var clone = spec.Clone();
            _infoCache[classType] = clone;
            return clone;
        }
        catch
        {
            return null; // 网络错误不当负面缓存：ComfyUI 可能刚重启，下次应再试
        }
        finally
        {
            _infoGate.Release();
        }
    }

    /// <summary>
    /// 查历史。已完成时返回该任务的输出图片清单（filename/subfolder/type）；
    /// 未完成返回空列表。历史里还带执行状态（status.status == "success"），
    /// 失败时能拿到 ComfyUI 自己的报错（缺模型 / 缺节点之类），直接抛给用户看。
    /// </summary>
    public async Task<List<ComfyOutputImage>> GetOutputImagesAsync(string promptId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"{await _cfg.GetBaseUrlAsync(ct)}/history/{promptId}", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return [];

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty(promptId, out var entry))
            return [];

        // 执行失败：ComfyUI 把异常放在 status.messages 里，中文提示比英文堆栈友好。
        // 注意历史里 status 的真实层级是 {"status_str": "success", "completed": true,
        // "messages": [...]}——completed 就在 status 对象下（不同版本字段名略有出入，
        // 这里只认 Object 型 status + 布尔 completed，不假设更深层级）。
        if (entry.TryGetProperty("status", out var statusEl) &&
            statusEl.ValueKind == JsonValueKind.Object &&
            statusEl.TryGetProperty("completed", out var completed) &&
            completed.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            !completed.GetBoolean())
        {
            var msg = "生成失败";
            if (statusEl.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                var errs = new List<string>();
                foreach (var m in messages.EnumerateArray())
                {
                    if (m.ValueKind == JsonValueKind.Array && m.GetArrayLength() >= 2)
                        errs.Add(m[1].ToString());
                }
                if (errs.Count > 0) msg = string.Join("；", errs);
            }
            throw new Exception($"ComfyUI：{Trim(msg)}");
        }

        var images = new List<ComfyOutputImage>();
        if (!entry.TryGetProperty("outputs", out var outputs)) return images;
        foreach (var node in outputs.EnumerateObject())
        {
            if (!node.Value.TryGetProperty("images", out var imgs)) continue;
            foreach (var img in imgs.EnumerateArray())
            {
                images.Add(new ComfyOutputImage(
                    img.GetProperty("filename").GetString() ?? "",
                    img.TryGetProperty("subfolder", out var sf) ? sf.GetString() ?? "" : "",
                    img.TryGetProperty("type", out var tp) ? tp.GetString() ?? "output" : "output"));
            }
        }
        return images;
    }

    /// <summary>下载成品图（字节流）。</summary>
    public async Task<byte[]> GetImageAsync(ComfyOutputImage img, CancellationToken ct = default)
    {
        var url = $"{await _cfg.GetBaseUrlAsync(ct)}/view?filename={Uri.EscapeDataString(img.Filename)}" +
                  $"&subfolder={Uri.EscapeDataString(img.Subfolder)}&type={Uri.EscapeDataString(img.Type)}";
        var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"ComfyUI 成品图下载失败（HTTP {(int)resp.StatusCode}）");
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>列出某加载器节点可选的模型（如 CheckpointLoaderSimple → ckpt_name 下拉）。</summary>
    public async Task<List<string>> GetModelChoicesAsync(string nodeType, string field, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{await _cfg.GetBaseUrlAsync(ct)}/object_info/{nodeType}", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return [];

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement.GetProperty(nodeType);
            if (!root.TryGetProperty("input", out var input)
                || !input.TryGetProperty("required", out var required)
                || !required.TryGetProperty(field, out var spec))
                return [];

            // spec 形如 [["模型A.safetensors","模型B.safetensors"], {...}]，第一项是候选值
            var first = spec[0];
            var list = new List<string>();
            foreach (var v in first.EnumerateArray()) list.Add(v.GetString() ?? "");
            return list;
        }
        catch { return []; }
    }

    /// <summary>http/https 基址转 ws/wss（WebSocket 专用）。</summary>
    private static string ToWebSocketUri(string baseUrl)
    {
        var u = baseUrl.TrimEnd('/');
        if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "wss://" + u["https://".Length..];
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "ws://" + u["http://".Length..];
        return "ws://" + u;
    }

    private static string Trim(string s, int max = 300)
    {
        s = s.Replace("\n", " ").Replace("\r", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}

/// <summary>WS 消息处理后的动作。</summary>
internal enum WsOutcome
{
    /// <summary>继续收下一条。</summary>
    Continue,
    /// <summary>本任务已执行完成。</summary>
    Done,
    /// <summary>WS 流中断（连接关闭），转 HTTP 轮询兜底。</summary>
    StreamEnded,
    /// <summary>执行失败（异常已由具体 case 抛出，此值仅作枚举完整性占位）。</summary>
    Failed
}

/// <summary>ComfyUI 历史里一张输出图的定位信息。</summary>
public record ComfyOutputImage(string Filename, string Subfolder, string Type);

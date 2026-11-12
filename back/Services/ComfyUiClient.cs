using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ERental.Options;
using Microsoft.Extensions.Options;

namespace ERental.Services;

///<summary>WS 推送的一次进度快照（Percent 0~99，100 留给完成态）</summary>
public record ComfyProgress(int Percent,string Stage);

///<summary>ComfyUI HTTP API 的薄封装</summary>
public class ComfyUiClient:IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly AiImageOptions _opt;
    private readonly ComfyConfigService _cfg;
    //object_info 缓存：同一批节点类型会被 schema 生成与提交前预检反复查询
    private readonly Dictionary<string,JsonElement?> _infoCache=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _infoGate=new(1,1);

    public ComfyUiClient(HttpClient http,IOptions<AiImageOptions> opt,ComfyConfigService cfg)
    {
        _http=http;
        _opt=opt.Value;
        _cfg=cfg;
        //BaseAddress 不在这里设：地址可在系统配置页动态修改，每次请求按生效值拼绝对 URI
        _http.Timeout=TimeSpan.FromSeconds(Math.Max(30,_opt.TimeoutSeconds));
    }

    ///<summary>当前生效的 ComfyUI 地址（配置页回显/离线提示用）</summary>
    public Task<string> EffectiveBaseUrlAsync(CancellationToken ct=default)=>_cfg.GetBaseUrlAsync(ct);

    ///<summary>探活：桌面版没启动时这里会抛异常，由上层翻译成「ComfyUI 未启动」</summary>
    public async Task<bool> IsOnlineAsync(CancellationToken ct=default)
    {
        try
        {
            var resp=await _http.GetAsync($"{await _cfg.GetBaseUrlAsync(ct)}/system_stats",ct);
            return resp.IsSuccessStatusCode;
        }
        catch{return false;}
    }

    ///<summary>上传参考图（图生图用），返回 ComfyUI 侧的文件名</summary>
    public async Task<string> UploadImageAsync(Stream content,string fileName,CancellationToken ct=default)
    {
        using var form=new MultipartFormDataContent();
        var fileContent=new StreamContent(content);
        fileContent.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent,"image",fileName);
        //overwrite=true
        form.Add(new StringContent("true"),"overwrite");

        var resp=await _http.PostAsync($"{await _cfg.GetBaseUrlAsync(ct)}/upload/image",form,ct);
        var body=await resp.Content.ReadAsStringAsync(ct);
        if(!resp.IsSuccessStatusCode) throw new BusinessException($"ComfyUI 上传参考图失败（HTTP {(int)resp.StatusCode}）：{Trim(body)}");

        using var doc=JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("name").GetString()??throw new BusinessException("ComfyUI 上传响应里没有文件名");
    }

    ///<summary>提交工作流，返回 prompt_id（后续凭它查历史/收 WS 进度）</summary>
    private async Task<string> SubmitPromptAsync(string promptJson,string? clientId=null,CancellationToken ct=default)
    {
        var payload=JsonSerializer.Serialize(new
        {
            prompt=JsonNode.Parse(promptJson),
            client_id=string.IsNullOrEmpty(clientId)?"erental":clientId
        });
        using var content=new StringContent(payload,Encoding.UTF8,"application/json");
        var resp=await _http.PostAsync($"{await _cfg.GetBaseUrlAsync(ct)}/prompt",content,ct);
        var body=await resp.Content.ReadAsStringAsync(ct);
        if(!resp.IsSuccessStatusCode) throw new BusinessException(TranslateSubmitError(body,(int)resp.StatusCode));

        using var doc=JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("prompt_id").GetString()??throw new BusinessException("ComfyUI 提交响应里没有 prompt_id");
    }

    ///<summary>提交并等待执行完成，返回 prompt_id</summary>
    public async Task<string> ExecutePromptAsync(
        string promptJson,IProgress<ComfyProgress>? progress,TimeSpan timeout,CancellationToken ct=default)
    {
        //超时统一在这里管：提交+执行+轮询一并计入，到点即抛中文超时
        using var timeoutCts=new CancellationTokenSource(timeout);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,timeoutCts.Token);
        var token=linked.Token;

        var baseUrl=await _cfg.GetBaseUrlAsync(token);
        //每次生成一个 clientId：WS 只收本任务的消息，多客户端并行时不串台
        var clientId=Guid.NewGuid().ToString("N");

        //① 尝试 WS 实时进度
        string promptId;
        bool wsCompleted;
        try
        {
            promptId=await SubmitPromptAsync(promptJson,clientId,token);
            progress?.Report(new ComfyProgress(1,"已提交"));
            wsCompleted=await ListenProgressAsync(baseUrl,clientId,promptId,progress,token);
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested)
        {
            //触发源是 timeoutCts（调用方没取消）→ 翻译成中文超时
            throw new BusinessException($"生成超时（{timeout.TotalSeconds:0}s 内未出图）。可换更小的尺寸/步数，或确认 ComfyUI 是否在正常执行。");
        }
        if(!wsCompleted)
        {
            progress?.Report(new ComfyProgress(5,"执行中（实时进度不可用，请耐心等待）"));
            await PollUntilDoneAsync(promptId,progress,token);
        }
        return promptId;
    }

    ///<summary>连 WS 收本任务的执行消息</summary>
    private static async Task<bool> ListenProgressAsync(string baseUrl,string clientId,string promptId,IProgress<ComfyProgress>? progress,CancellationToken ct)
    {
        using var ws=new ClientWebSocket();
        ws.Options.KeepAliveInterval=TimeSpan.FromSeconds(20);
        try
        {
            using var connectCts=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            //注意路径必须是 /ws：漏掉路径时请求会打到 /，ComfyUI 的升级处理器只认 /ws
            var wsUri=$"{ToWebSocketUri(baseUrl)}/ws?clientId={clientId}";
            await ws.ConnectAsync(new Uri(wsUri),connectCts.Token);
        }
        catch
        {
            return false;//WS 不可用：老版本 ComfyUI 或网络策略挡了升级请求
        }

        var buffer=new byte[64*1024];
        while(true)
        {
            var text=await ReceiveFrameTextAsync(ws,buffer,ct);
            if(text is null) return false;//流断了 → 轮询兜底
            if(text.Length==0) continue;//纯二进制帧（预览图/音频），读下一条
            var outcome=HandleWsMessage(text,promptId,progress);
            if(outcome==WsOutcome.Done) return true;
            if(outcome==WsOutcome.StreamEnded) return false;
            if(outcome==WsOutcome.Failed) throw new BusinessException("生成失败");//占位，下面替换为具体报错
            //其余（Continue/未知）：本条只是中间帧，继续读下一条
        }
    }

    ///<summary>收一整条 WS 消息的文本</summary>
    private static async Task<string?> ReceiveFrameTextAsync(ClientWebSocket ws,byte[] buffer,CancellationToken ct)
    {
        using var message=new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result=await ws.ReceiveAsync(buffer,ct);
            if(result.MessageType==WebSocketMessageType.Close) return null;
            if(result.MessageType==WebSocketMessageType.Text) await message.WriteAsync(buffer.AsMemory(0,result.Count),ct);
            //二进制帧是预览图/音频，进度用不上，收下即弃
        }
        while(!result.EndOfMessage);

        return message.Length==0?string.Empty:Encoding.UTF8.GetString(message.ToArray());
    }

    private static WsOutcome HandleWsMessage(string text,string promptId,IProgress<ComfyProgress>? progress)
    {
        JsonNode? node;
        try{node=JsonNode.Parse(text);}
        catch{return WsOutcome.Continue;} //非 JSON 帧（极少见），忽略
        if(node is not JsonObject obj) return WsOutcome.Continue;

        var type=obj["type"]?.GetValue<string>();
        if(obj["data"] is not JsonObject data) return WsOutcome.Continue;

        //多客户端场景：带了 prompt_id 且不是本任务的
        var msgPid=data["prompt_id"]?.GetValue<string>();
        if(msgPid!=null&&msgPid!=promptId) return WsOutcome.Continue;

        switch(type)
        {
            case "execution_start":
                progress?.Report(new ComfyProgress(2,"开始执行"));
                break;
            case "execution_cached":
                progress?.Report(new ComfyProgress(10,"命中缓存节点，跳过重复计算"));
                break;
            case "executing":
                var nodeId=data["node"]?.GetValue<string>();
                if(nodeId is null)
                {
                    //executing(null)=所有节点跑完（其后还会有 execution_success）
                    progress?.Report(new ComfyProgress(95,"节点执行完毕，正在保存"));
                    return WsOutcome.Done;
                }
                progress?.Report(new ComfyProgress(15,$"执行节点 {nodeId}"));
                break;
            case "progress":
                //ComfyUI 的采样步进：value/max 是当前节点的步数
                var value=data["value"]?.GetValue<double>()??0;
                var max=data["max"]?.GetValue<double>()??0;
                if(max>0)
                    progress?.Report(new ComfyProgress((int)Math.Round(value/max*100),$"采样中 {(long)value}/{(long)max} 步"));
                break;
            case "execution_success":
                progress?.Report(new ComfyProgress(99,"生成完成，正在下载"));
                return WsOutcome.Done;
            case "execution_error":
                throw BuildExecutionError(data);
            case "execution_interrupted":
                throw new BusinessException("生成已被中断（ComfyUI 侧取消了本次执行）");
            case "status":
                var queueLeft=data["status"]?["exec_info"]?["queue_remaining"]?.GetValue<int>()??0;
                if(queueLeft>0)
                    progress?.Report(new ComfyProgress(1,$"排队中（前方还有 {queueLeft} 个任务）"));
                break;
        }
        return WsOutcome.Continue;
    }

    ///<summary>把 ComfyUI 的 execution_error 数据翻成中文异常（节点定位+异常原文）</summary>
    private static Exception BuildExecutionError(JsonObject data)
    {
        var nodeId=data["node_id"]?.GetValue<string>()??"";
        var nodeType=data["node_type"]?.GetValue<string>()??"";
        var message=data["exception_message"]?.GetValue<string>()??"";
        var exType=data["exception_type"]?.GetValue<string>()??"";

        var line="ComfyUI 执行失败";
        if(nodeId.Length>0) line+=$"（节点 {nodeId} {nodeType}）".TrimEnd();
        if(message.Length>0) line+=$"：{Trim(message)}";
        else if(exType.Length>0) line+=$"：{exType}";
        return new Exception(line);
    }

    ///<summary>WS 不可用时的兜底：轮询历史直到出图（到点由 CancellationToken 抛出）</summary>
    private async Task PollUntilDoneAsync(string promptId,IProgress<ComfyProgress>? progress,CancellationToken ct)
    {
        while(true)
        {
            await Task.Delay(Math.Max(200,_opt.PollIntervalMs),ct);
            var outputs=await GetOutputImagesAsync(promptId,ct);
            if(outputs.Count>0)
            {
                progress?.Report(new ComfyProgress(99,"生成完成，正在下载"));
                return;
            }
        }
    }

    ///<summary>把 ComfyUI 的 400 校验错误翻译成中文</summary>
    private static string TranslateSubmitError(string body,int status)
    {
        try
        {
            using var doc=JsonDocument.Parse(body);
            var lines=CollectErrorLines(doc.RootElement);
            if(lines.Count>0) return $"ComfyUI 拒绝工作流（HTTP {status}）：{string.Join("；", lines)}";
        }
        catch{/* 响应不是 JSON，落回原始文本 */}

        return $"ComfyUI 拒绝工作流（HTTP {status}）：{Trim(body)}";
    }

    ///<summary>逐条收集校验错误：优先「按节点」逐条说明，一条都取不到时退回顶层 error</summary>
    private static List<string> CollectErrorLines(JsonElement root)
    {
        var lines=CollectNodeErrorLines(root);
        if(lines.Count==0)
        {
            var topLevel=ReadTopLevelError(root);
            if(topLevel!=null) lines.Add(topLevel);
        }
        return lines;
    }

    private static List<string> CollectNodeErrorLines(JsonElement root)
    {
        var lines=new List<string>();
        if(!root.TryGetProperty("node_errors",out var nodeErrors)||nodeErrors.ValueKind!=JsonValueKind.Object) return lines;

        foreach(var node in nodeErrors.EnumerateObject())
        {
            if(!node.Value.TryGetProperty("errors",out var errors)) continue;
            var classType=node.Value.TryGetProperty("class_type",out var ctProp)?ctProp.GetString()??"":"";
            foreach(var err in errors.EnumerateArray()) lines.Add(DescribeNodeError(node.Name,classType,err));
        }
        return lines;
    }

    ///<summary>单条节点错误：节点定位+字段名+中文原因+ComfyUI 原文细节（截断到 120 字）</summary>
    private static string DescribeNodeError(string nodeId,string classType,JsonElement err)
    {
        var type=err.TryGetProperty("type",out var t)?t.GetString()??"":"";
        var details=err.TryGetProperty("details",out var d)?d.GetString()??"":"";
        var field=err.TryGetProperty("extra_info",out var ei)&&ei.TryGetProperty("input_name",out var inm)?inm.GetString():null;

        var line=$"节点 {nodeId}（{classType}）";
        if(!string.IsNullOrEmpty(field)) line+=$" 的 {field}";
        line+=type switch
        {
            "value_not_in_list"=>" 取值不在可选列表（文件不存在或文件名不符）",
            "required_input_missing"=>" 缺少必填输入",
            "invalid_type"=>" 类型不正确",
            "too_long" or "too_short"=>" 长度超出限制",
            "not_a_number"=>" 不是有效数字",
            _=>$" 校验失败（{type}）"
        };
        if(!string.IsNullOrEmpty(details)) line+=$"：{Trim(details, 120)}";
        return line;
    }

    ///<summary>顶层 error 对象（node_errors 为空时才有意义）：message+截断后的 details</summary>
    private static string? ReadTopLevelError(JsonElement root)
    {
        if(!root.TryGetProperty("error",out var error)||error.ValueKind!=JsonValueKind.Object) return null;
        var msg=error.TryGetProperty("message",out var em)?em.GetString()??"":"";
        var det=error.TryGetProperty("details",out var ed)?ed.GetString()??"":"";
        if(msg.Length==0&&det.Length==0) return null;
        return msg+(det.Length>0?$"：{Trim(det, 150)}":"");
    }

    ///<summary>拉取某节点类型的输入定义（object_info），供提交前预检与表单 schema 生成用</summary>
    public async Task<JsonElement?> GetNodeInfoAsync(string classType,CancellationToken ct=default)
    {
        if(_infoCache.TryGetValue(classType,out var cached)) return cached;
        await _infoGate.WaitAsync(ct);
        try
        {
            if(_infoCache.TryGetValue(classType,out cached)) return cached;
            var resp=await _http.GetAsync($"{await _cfg.GetBaseUrlAsync(ct)}/object_info/{Uri.EscapeDataString(classType)}",ct);
            if(!resp.IsSuccessStatusCode)
            {
                _infoCache[classType]=null;
                return null;
            }
            var body=await resp.Content.ReadAsStringAsync(ct);
            using var doc=JsonDocument.Parse(body);
            if(!doc.RootElement.TryGetProperty(classType,out var spec))
            {
                _infoCache[classType]=null;
                return null;
            }
            var clone=spec.Clone();
            _infoCache[classType]=clone;
            return clone;
        }
        catch
        {
            return null;//网络错误不当负面缓存：ComfyUI 可能刚重启，下次应再试
        }
        finally
        {
            _infoGate.Release();
        }
    }

    ///<summary>查历史</summary>
    public async Task<List<ComfyOutputImage>> GetOutputImagesAsync(string promptId,CancellationToken ct=default)
    {
        var resp=await _http.GetAsync($"{await _cfg.GetBaseUrlAsync(ct)}/history/{promptId}",ct);
        var body=await resp.Content.ReadAsStringAsync(ct);
        if(!resp.IsSuccessStatusCode) return[];

        using var doc=JsonDocument.Parse(body);
        if(!doc.RootElement.TryGetProperty(promptId,out var entry)) return[];

        //执行失败：ComfyUI 把异常放在 status.messages 里
        var failure=ReadStatusFailure(entry);
        if(failure!=null) throw new BusinessException($"ComfyUI：{Trim(failure)}");

        return ReadOutputImages(entry);
    }

    ///<summary>历史记录的失败原因：只在 status 是对象、completed 明确为 false 时返回非空</summary>
    private static string? ReadStatusFailure(JsonElement entry)
    {
        if(!entry.TryGetProperty("status",out var statusEl)||statusEl.ValueKind!=JsonValueKind.Object) return null;
        if(!statusEl.TryGetProperty("completed",out var completed)||completed.ValueKind is not(JsonValueKind.True or JsonValueKind.False)) return null;
        if(completed.GetBoolean()) return null;

        if(!statusEl.TryGetProperty("messages",out var messages)||messages.ValueKind!=JsonValueKind.Array) return "生成失败";

        //messages 形如[["execution_error",{...}]
        var errs=new List<string>();
        foreach(var m in messages.EnumerateArray())
        {
            if(m.ValueKind==JsonValueKind.Array&&m.GetArrayLength()>=2) errs.Add(m[1].ToString());
        }
        return errs.Count>0?string.Join("；",errs):"生成失败";
    }

    ///<summary>从历史记录的 outputs 里摊平所有输出图片</summary>
    private static List<ComfyOutputImage> ReadOutputImages(JsonElement entry)
    {
        var images=new List<ComfyOutputImage>();
        if(!entry.TryGetProperty("outputs",out var outputs)) return images;

        foreach(var node in outputs.EnumerateObject())
        {
            if(!node.Value.TryGetProperty("images",out var imgs)) continue;
            foreach(var img in imgs.EnumerateArray()) images.Add(ReadOutputImage(img));
        }
        return images;
    }

    private static ComfyOutputImage ReadOutputImage(JsonElement img)=>new(
        img.GetProperty("filename").GetString()??"",
        img.TryGetProperty("subfolder",out var sf)?sf.GetString()??"":"",
        img.TryGetProperty("type",out var tp)?tp.GetString()??"output":"output");

    ///<summary>下载成品图（字节流）</summary>
    public async Task<byte[]> GetImageAsync(ComfyOutputImage img,CancellationToken ct=default)
    {
        var url=$"{await _cfg.GetBaseUrlAsync(ct)}/view?filename={Uri.EscapeDataString(img.Filename)}"+$"&subfolder={Uri.EscapeDataString(img.Subfolder)}&type={Uri.EscapeDataString(img.Type)}";
        var resp=await _http.GetAsync(url,ct);
        if(!resp.IsSuccessStatusCode) throw new BusinessException($"ComfyUI 成品图下载失败（HTTP {(int)resp.StatusCode}）");
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    ///<summary>列出某加载器节点可选的模型</summary>
    public async Task<List<string>> GetModelChoicesAsync(string nodeType,string field,CancellationToken ct=default)
    {
        try
        {
            var resp=await _http.GetAsync($"{await _cfg.GetBaseUrlAsync(ct)}/object_info/{nodeType}",ct);
            var body=await resp.Content.ReadAsStringAsync(ct);
            if(!resp.IsSuccessStatusCode) return[];

            using var doc=JsonDocument.Parse(body);
            var root=doc.RootElement.GetProperty(nodeType);
            if(!root.TryGetProperty("input",out var input)||!input.TryGetProperty("required",out var required)||!required.TryGetProperty(field,out var spec)) return[];

            //spec 形如[["模型A.safetensors","模型B.safetensors"]
            var first=spec[0];
            var list=new List<string>();
            foreach(var v in first.EnumerateArray()) list.Add(v.GetString()??"");
            return list;
        }
        catch{return[];}
    }

    ///<summary>http/https 基址转 ws/wss（WebSocket 专用）</summary>
    private static string ToWebSocketUri(string baseUrl)
    {
        var u=baseUrl.TrimEnd('/');
        if(u.StartsWith("https://",StringComparison.OrdinalIgnoreCase)) return "wss://"+u["https://".Length..];
        if(u.StartsWith("http://",StringComparison.OrdinalIgnoreCase)) return "ws://"+u["http://".Length..];
        return "ws://"+u;
    }

    private static string Trim(string s,int max=300)
    {
        s=s.Replace("\n"," ").Replace("\r"," ").Trim();
        return s.Length<=max?s:s[..max]+"…";
    }

    ///<summary>释放 object_info 缓存的信号量</summary>
    public ValueTask DisposeAsync()
    {
        _infoGate.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

///<summary>WS 消息处理后的动作</summary>
internal enum WsOutcome
{
    ///<summary>继续收下一条</summary>
    Continue,
    ///<summary>本任务已执行完成</summary>
    Done,
    ///<summary>WS 流中断（连接关闭），转 HTTP 轮询兜底</summary>
    StreamEnded,
    ///<summary>执行失败</summary>
    Failed
}

///<summary>ComfyUI 历史里一张输出图的定位信息</summary>
public record ComfyOutputImage(string Filename,string Subfolder,string Type);

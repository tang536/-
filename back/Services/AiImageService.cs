using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ERental.DTOs;
using ERental.Options;
using Microsoft.Extensions.Options;

namespace ERental.Services;

/// <summary>
/// AI 生图服务：把用户在页面里填的参数，「打补丁」到他自己的 ComfyUI 工作流模板上，
/// 提交给本机 ComfyUI 桌面版执行，再把成品图下载落盘。
///
/// 设计要点：
///  ① 模板即用户在桌面版里手工调好的工作流（Save 为 API Format 后放进
///     back/ComfyWorkflows/*.json）——模型、采样器、放大、保存节点全部尊重用户原稿，
///     本服务只按 class_type 精准改几个输入框，不重排图、不删节点；
///  ② 补丁规则全部按 class_type 识别（CLIPTextEncode / EmptyLatentImage / KSampler /
///     CheckpointLoaderSimple / UNETLoader / LoadImage），兼容 SD1.5 / SDXL / Flux /
///     GGUF 各类主流图，不猜节点 id、不依赖特定模板；
///  ③ 生成是长任务（本地 4GB 显卡一张图几十秒到几分钟），轮询 + 总体超时，
///     失败时把 ComfyUI 自己的报错（缺模型/缺节点/OOM）原样带给用户。
/// </summary>
public class AiImageService(
    ComfyUiClient comfy, ImageStorage images, IOptions<AiImageOptions> opt,
    AiProgressTracker tracker, ILogger<AiImageService> logger)
{
    private readonly ComfyUiClient _comfy = comfy;
    private readonly ImageStorage _images = images;
    private readonly AiProgressTracker _tracker = tracker;
    private readonly ILogger<AiImageService> _logger = logger;

    private readonly AiImageOptions _opt = opt.Value;

    /// <summary>模板目录绝对路径（不存在则返回配置值拼出的路径，由调用方判在否）。</summary>
    private string TemplatesDir =>
        Path.IsPathRooted(_opt.WorkflowsDir)
            ? _opt.WorkflowsDir
            : Path.Combine(Directory.GetCurrentDirectory(), _opt.WorkflowsDir);

    /// <summary>可用模板名（.json 文件名不含扩展名，按名称排序）。</summary>
    public List<string> ListTemplates()
    {
        var dir = TemplatesDir;
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.GetFiles(dir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<AiImageStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var status = new AiImageStatus
        {
            Templates = ListTemplates(),
            Online = await _comfy.IsOnlineAsync(ct)
        };
        if (!status.Online)
        {
            status.Message = $"ComfyUI 未启动或地址不通（{await _comfy.EffectiveBaseUrlAsync(ct)}）。" +
                "请先打开 Comfy Desktop 并等待界面加载完成；地址变更可在「系统配置」页更新。";
            return status;
        }
        // 模型下拉：CheckpointLoaderSimple（safetensors 主模型）与 UNETLoader（GGUF）各拉一份，
        // 用户模板用哪类节点，前端就展示哪一份。
        status.Checkpoints = await _comfy.GetModelChoicesAsync("CheckpointLoaderSimple", "ckpt_name", ct);
        status.DiffusionModels = await _comfy.GetModelChoicesAsync("UNETLoader", "unet_name", ct);
        return status;
    }

    /// <summary>
    /// 生成模板的表单 schema：遍历模板里的每个节点，对照 ComfyUI object_info 的输入定义，
    /// 把「可填的输入」逐个暴露成表单字段——模型、LoRA、步数、CFG、宽高、多区提示词、
    /// shift、batch_size……工作流里有什么就填什么，与节点 id 无关。
    /// ComfyUI 离线时 object_info 拿不到：字段仍在（默认值来自模板），只是下拉选项为空。
    /// </summary>
    public async Task<AiTemplateSchema> BuildSchemaAsync(string template, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(template)) throw new Exception("请选择工作流模板");
        var templatePath = Path.Combine(TemplatesDir, template + ".json");
        if (!File.Exists(templatePath))
            throw new Exception($"模板不存在：{template}.json（应放在 back/ComfyWorkflows/ 目录）");

        var graphNode = LoadTemplate(templatePath);
        ValidateTemplateShape(graphNode);
        if (graphNode is not JsonObject graph) throw new Exception("工作流图结构异常");

        var fields = new List<AiFieldDef>();
        var anyInfo = false;      // object_info 是否至少可用一次
        var emptyCombos = 0;      // 有字段定义但下拉为空的个数（模型目录为空；参考图不算）
        var promptRequiredDone = false; // 首个提示词节点已标必填（负向/多区提示词不标）

        foreach (var (key, value) in graph)
        {
            if (value is not JsonObject node) continue;
            var classType = node["class_type"]?.GetValue<string>() ?? "";
            if (classType.Length == 0) continue;
            if (classType is "MarkdownNote" or "Reroute" or "Note") continue; // 装饰节点，无输入定义

            var spec = await _comfy.GetNodeInfoAsync(classType, ct);
            if (spec is null) continue; // 未注册节点不暴露（提交前 CheckNodeTypesAsync 会报缺插件）
            if (!spec.Value.TryGetProperty("input", out var inputDef)) continue;
            anyInfo = true;

            var title = node["_meta"]?["title"]?.GetValue<string>() ?? "";
            var inputs = node["inputs"] as JsonObject;

            foreach (var section in new[] { "required", "optional" })
            {
                if (!inputDef.TryGetProperty(section, out var sectionDef) ||
                    sectionDef.ValueKind != JsonValueKind.Object) continue;

                foreach (var prop in sectionDef.EnumerateObject())
                {
                    var field = prop.Name;
                    var fieldSpec = prop.Value;
                    if (fieldSpec.ValueKind != JsonValueKind.Array || fieldSpec.GetArrayLength() == 0) continue;

                    // 连线型输入（["MODEL"]/["CONDITIONING"]/["LATENT"]……）：首元素是字符串
                    // 且不在已知控件类型里——这些由工作流连线决定，不暴露给用户填。
                    if (fieldSpec[0].ValueKind != JsonValueKind.Array)
                    {
                        var t0 = fieldSpec[0].GetString() ?? "";
                        if (t0 is not ("INT" or "FLOAT" or "STRING" or "BOOLEAN")) continue;
                    }

                    var def = new AiFieldDef
                    {
                        Key = $"{key}.{field}",
                        Label = BuildLabel(title, classType, field),
                        Group = GroupOf(classType, field),
                        NodeType = classType,
                        InputName = field
                    };
                    // 模板里的当前值作为默认值；该输入在模板里是连线（极少数 optional 混连）则跳过
                    var current = inputs?[field];
                    if (current is JsonArray) continue;
                    def.Default = current switch
                    {
                        JsonValue jv when jv.TryGetValue(out string? s) => s,
                        JsonValue jv when jv.TryGetValue(out bool b) => b,
                        JsonValue jv when jv.TryGetValue(out long l) => l,
                        JsonValue jv when jv.TryGetValue(out double d) => d,
                        _ => null
                    };

                    if (fieldSpec[0].ValueKind == JsonValueKind.Array)
                    {
                        // 下拉：第一项是候选值数组（模型清单/lora清单/采样器名……）
                        def.Kind = "select";
                        def.Options = [.. fieldSpec[0].EnumerateArray()
                            .Where(o => o.ValueKind == JsonValueKind.String)
                            .Select(o => o.GetString() ?? "")];
                        if (def.Options.Count == 0 && def.Group != "image") emptyCombos++;
                    }
                    else
                    {
                        var typeName = fieldSpec[0].GetString() ?? "";
                        var opts = fieldSpec.GetArrayLength() > 1 && fieldSpec[1].ValueKind == JsonValueKind.Object
                            ? fieldSpec[1] : (JsonElement?)null;
                        switch (typeName)
                        {
                            case "INT":
                                def.Kind = field is "seed" or "noise_seed" ? "seed" : "int";
                                break;
                            case "FLOAT":
                                def.Kind = "float";
                                break;
                            case "STRING":
                                def.Kind = opts?.TryGetProperty("multiline", out var ml) == true &&
                                           ml.ValueKind == JsonValueKind.True ? "textarea" : "text";
                                break;
                            case "BOOLEAN":
                                def.Kind = "boolean";
                                def.Options = ["true", "false"];
                                break;
                            default:
                                continue;
                        }
                        // 第 3 段是节点的「额外配置」对象（min/max/step 等），可能不存在。
                        // 这里用空值检查而不是 `opts is JsonElement o`：opts 的静态类型已经是
                        // JsonElement?，非 null 时必定是该类型，再写一次类型检查不带来任何新信息。
                        if (opts is not null)
                        {
                            var o = opts.Value;
                            if (o.TryGetProperty("min", out var mn) && mn.ValueKind == JsonValueKind.Number) def.Min = mn.GetDouble();
                            if (o.TryGetProperty("max", out var mx) && mx.ValueKind == JsonValueKind.Number) def.Max = mx.GetDouble();
                            if (o.TryGetProperty("step", out var st) && st.ValueKind == JsonValueKind.Number) def.Step = st.GetDouble();
                        }
                    }

                    // 提示词是唯一必填项，且只标第一个文本节点（正向提示词）；
                    // 负向/多区提示词留空 = 保留模板原值，不标必填（前端也只校验这一个）
                    if (classType == "CLIPTextEncode" && field == "text" && !promptRequiredDone)
                    {
                        def.Required = true;
                        promptRequiredDone = true;
                    }
                    fields.Add(def);
                }
            }
        }

        // 分组排序：提示词 → 模型 → 尺寸 → 采样 → 图片 → 输出 → 其他；组内按 key 稳定
        var order = new[] { "prompt", "model", "size", "sampler", "image", "output", "other" };
        fields = [.. fields
            .OrderBy(f => Array.IndexOf(order, f.Group))
            .ThenBy(f => f.Key, StringComparer.Ordinal)];

        var schema = new AiTemplateSchema { Template = template, Fields = fields };
        if (!anyInfo)
            schema.Notice = "ComfyUI 离线或 object_info 不可用：下拉选项为空。可先按模板默认值填写，启动 ComfyUI 后刷新面板即可看到可选项。";
        else if (emptyCombos > 0)
            schema.Notice = $"有 {emptyCombos} 个下拉选项为空——对应模型目录里还没有文件。可先去 ComfyUI Model Manager 下载模型，或使用其他已就绪的参数。";
        return schema;
    }

    /// <summary>字段标签：优先用用户给节点起的标题（「正向提示词」），否则「类名 · 输入名」。</summary>
    private static string BuildLabel(string title, string classType, string field)
    {
        var baseName = !string.IsNullOrWhiteSpace(title) && title != classType ? title : classType;
        return $"{baseName} · {field}";
    }

    /// <summary>按输入名归类（下拉按类名前缀归一化），前端按组分区渲染。</summary>
    private static string GroupOf(string classType, string field)
    {
        var f = field.ToLowerInvariant();
        if (classType == "CLIPTextEncode" && f == "text") return "prompt";
        if (f is "ckpt_name" or "unet_name" or "unet_name_high_noise" or "unet_name_low_noise"
            or "gguf_name" or "vae_name" or "clip_name" or "clip_name1" or "clip_name2"
            or "clip_name3" or "clip_name4" or "clip_vision_name" or "lora_name"
            or "style_model_name" or "control_net_name" or "gligen_name" or "photomaker_name"
            or "model_name" or "diffusion_model_name" or "weight_dtype" or "device" or "type")
            return "model";
        if (f is "width" or "height") return "size";
        if (f is "seed" or "noise_seed" or "steps" or "cfg" or "denoise" or "sampler_name"
            or "scheduler" or "sigma_max" or "sigma_min" or "shift") return "sampler";
        if (f is "image" or "image1" or "image2") return "image";
        return f is "filename_prefix" ? "output" : "other";
    }

    /// <summary>图里是否存在指定 class_type 的节点。</summary>
    private static bool HasNodeType(JsonObject graph, string classType)
    {
        foreach (var (_, value) in graph)
        {
            if (value is JsonObject n && (n["class_type"]?.GetValue<string>() ?? "") == classType) return true;
        }
        return false;
    }

    /// <summary>找第一个指定 class_type 的节点（同类多个时取图里顺序第一个）。</summary>
    private static JsonObject? FindNode(JsonObject graph, string classType)
    {
        foreach (var (_, value) in graph)
        {
            if (value is JsonObject n && (n["class_type"]?.GetValue<string>() ?? "") == classType) return n;
        }
        return null;
    }

    /// <summary>
    /// 通用补丁通道：按 schema 的「节点id.输入名」键精准覆写模板输入值。
    /// 类型按模板里该输入的现值推断（数字写数字、字符串写字符串、布尔写布尔），
    /// 连线型输入（数组占位）不碰。前端把所有非空字段一次性放进 Inputs 字典。
    /// </summary>
    private static void ApplyInputs(JsonObject root, Dictionary<string, object?> inputs)
    {
        foreach (var (key, value) in inputs)
        {
            if (value is null) continue;
            var dot = key.IndexOf('.');
            if (dot <= 0 || dot == key.Length - 1) continue;
            if (root[key[..dot]] is not JsonObject node) continue;
            if (node["inputs"] is not JsonObject ins) continue;
            var field = key[(dot + 1)..];
            var current = ins[field];
            if (current is JsonArray) continue; // 连线输入不可覆写
            var converted = ConvertToJsonValue(value, current);
            if (converted != null) ins[field] = converted;
        }
    }

    /// <summary>把页面传来的字符串/数字/JsonElement 转成与模板输入类型一致的 JSON 值。</summary>
    private static JsonNode? ConvertToJsonValue(object? value, JsonNode? current)
    {
        var s = value switch
        {
            null => null,
            string str => str,
            bool b => b ? "true" : "false",
            double d => d.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            int or long or decimal => value.ToString(),
            JsonElement je => je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => je.ToString(),
                // 非标量 JSON（对象/数组/未定义/Null）无法转成单个输入值，一律置空
                JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null => null,
                // 兜底：枚举若出现未命名取值同样置空（同时消除 CS8524 非穷尽告警）
                _ => null
            },
            _ => value.ToString()
        };
        if (string.IsNullOrWhiteSpace(s)) return null;

        // 优先按模板现值定型，模板没有该输入时按字面推断。
        // 顺序：字符串 → 布尔 → 数字。数字里「带小数点」或「模板现值是浮点」的
        // 一律走 double，避免 shift=3 的模板被填 4.5 时落进 long 分支写不进去。
        if (current is JsonValue cv)
        {
            if (cv.TryGetValue(out string? _)) return s;
            if (cv.TryGetValue(out bool cb)) return bool.TryParse(s, out var b) ? b : cb;
            var wantDouble = s.Contains('.') || s.Contains('e') || s.Contains('E') ||
                (cv.TryGetValue(out double cd0) && Math.Abs(cd0 % 1) > double.Epsilon);
            if (wantDouble)
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : cv;
            if (cv.TryGetValue(out long _))
                return long.TryParse(s, out var l) ? l : cv;
            if (cv.TryGetValue(out double _))
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var dAny) ? dAny : cv;
        }
        if (long.TryParse(s, out var l2)) return l2;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d2)) return d2;
        if (bool.TryParse(s, out var b2)) return b2;
        return s;
    }

    /// <summary>种子填 -1 = 随机：覆写后统一替换所有采样器的 seed/noise_seed。</summary>
    private static void RandomizeSeedIfRequested(JsonObject root)
    {
        foreach (var (_, value) in root)
        {
            if (value is not JsonObject node) continue;
            if (node["inputs"] is not JsonObject ins) continue;
            foreach (var field in new[] { "seed", "noise_seed" })
            {
                if (ins[field] is JsonValue v && v.TryGetValue(out long sv) && sv < 0)
                    ins[field] = Random.Shared.NextInt64(0, 2_000_000_000);
            }
        }
    }

    /// <summary>补丁后回读（生效种子, 宽, 高）供结果回显——读图里的真实值，不猜。</summary>
    private static (long seed, int width, int height) ReadBackPatchResults(JsonObject root)
    {
        var seed = Random.Shared.NextInt64(0, 2_000_000_000);
        var width = 0;
        var height = 0;
        JsonObject? sampler = null, sizeNode = null;
        foreach (var (_, value) in root)
        {
            if (value is not JsonObject node) continue;
            switch (node["class_type"]?.GetValue<string>() ?? "")
            {
                case "KSampler":
                case "KSamplerAdvanced":
                    sampler ??= node; break;
                case "EmptyLatentImage":
                case "EmptySD3LatentImage":
                case "ImageScale":
                    sizeNode ??= node; break;
            }
        }
        if (sampler?["inputs"] is JsonObject si)
        {
            if (si["seed"] is JsonValue s1 && s1.TryGetValue(out long v1)) seed = v1;
            else if (si["noise_seed"] is JsonValue s2 && s2.TryGetValue(out long v2)) seed = v2;
        }
        if (sizeNode?["inputs"] is JsonObject zi)
        {
            width = ReadInt(zi["width"]);
            height = ReadInt(zi["height"]);
        }
        return (seed, width, height);
    }

    /// <summary>从 JSON 值读整数（补丁写入的可能是 long/double，逐个尝试）。</summary>
    private static int ReadInt(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue(out long l)) return (int)l;
            if (v.TryGetValue(out int i)) return i;
            if (v.TryGetValue(out double d)) return (int)d;
        }
        return 0;
    }

    /// <summary>
    /// 生成入口。imgStream != null 时为图生图（先上传参考图，再补丁 LoadImage 节点）。
    /// </summary>
    public async Task<AiImageResult> GenerateAsync(AiImageRequest req, Stream? imgStream, string? imgFileName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Template)) throw new Exception("请选择工作流模板");

        var templatePath = Path.Combine(TemplatesDir, req.Template + ".json");
        if (!File.Exists(templatePath))
            throw new Exception($"模板不存在：{req.Template}.json（应放在 back/ComfyWorkflows/ 目录）");

        var sw = Stopwatch.StartNew();

        // 图生图：先把参考图传进 ComfyUI 的 input 目录，拿到服务端文件名
        string? uploadedName = null;
        if (imgStream != null)
        {
            if (string.IsNullOrWhiteSpace(imgFileName)) imgFileName = "reference.png";
            uploadedName = await _comfy.UploadImageAsync(imgStream, imgFileName, ct);
        }

        // 载入模板 → ① 格式体检 → ② 节点注册检查 → ③ 打补丁 → ④ 输入与取值预检 → 提交
        // 顺序有意为之：格式与节点类型问题与补丁无关，必须先说，否则会被
        // 「找不到 CLIPTextEncode」这类补丁错误挡住，把用户引向错误的方向。
        var graphNode = LoadTemplate(templatePath);
        ValidateTemplateShape(graphNode);
        if (graphNode is not JsonObject graph) throw new Exception("工作流图结构异常");
        // 模板里确实有提示词节点时才要求填提示词（纯视频类工作流没有 CLIPTextEncode）
        if (string.IsNullOrWhiteSpace(req.Prompt) && HasNodeType(graph, "CLIPTextEncode"))
            throw new Exception("请填写提示词");

        var infos = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        await CheckNodeTypesAsync(graph, infos, ct);

        // ③ 打补丁：schema 驱动（Inputs 非空）走通用通道，按「节点id.输入名」精准覆写；
        // 没带 Inputs 的老调用走固定字段兜底（Prompt/Width/Steps/……）。
        long seed; int width; int height;
        if (req.Inputs is { Count: > 0 } inputs)
        {
            ApplyInputs(graph, inputs);
            // 图生图：上传的参考图覆写 LoadImage（无论 Inputs 里有没有带它）
            var loadNode = FindNode(graph, "LoadImage");
            if (!string.IsNullOrEmpty(uploadedName) && loadNode is not null)
                SetInput(loadNode, "image", uploadedName);
            // 种子填 -1 = 随机：统一在覆写后替换，保证可复现语义对所有采样器生效
            RandomizeSeedIfRequested(graph);
            (seed, width, height) = ReadBackPatchResults(graph);
        }
        else
        {
            (seed, width, height) = PatchGraph(graph, req, uploadedName);
        }

        // trustedFiles：刚被 ComfyUI 接收的上传文件，object_info 若因缓存未列出也不误报
        var trusted = string.IsNullOrEmpty(uploadedName) ? null : new HashSet<string> { uploadedName };
        await PreflightAsync(graph, infos, trusted, ct);
        // 提交并等待执行完成：首选 ComfyUI WebSocket 实时进度（排队/节点/采样步数/失败原因），
        // WS 连不上时自动退回历史轮询。进度经 jobId 写进跟踪器，前端轮询渲染进度条。
        var jobId = (req.JobId ?? "").Trim();
        if (jobId.Length > 0) _tracker.Begin(jobId);
        IProgress<ComfyProgress>? progress = jobId.Length > 0
            ? new Progress<ComfyProgress>(p => _tracker.Report(jobId, p.Percent, p.Stage))
            : null;

        List<ComfyOutputImage> outputs;
        try
        {
            var promptId = await _comfy.ExecutePromptAsync(
                graph.ToJsonString(), progress, TimeSpan.FromSeconds(Math.Max(30, _opt.TimeoutSeconds)), ct);
            // 执行成功后再查一次历史拿输出图片清单（此时历史里必定有）
            outputs = await _comfy.GetOutputImagesAsync(promptId, ct);
            if (outputs.Count == 0)
                throw new Exception("ComfyUI 报告执行成功，但历史里没有输出图片（可重试一次）");
        }
        catch (Exception ex)
        {
            if (jobId.Length > 0) _tracker.Fail(jobId, ex.Message);
            throw;
        }

        // 下载第一张成品并落盘到 uploads/ai/
        var bytes = await _comfy.GetImageAsync(outputs[0], ct);
        if (bytes.Length == 0) throw new Exception("ComfyUI 返回了空图片");
        await using var ms = new MemoryStream(bytes);
        var url = await _images.SaveStreamAsync(ms, "ai", InferExt(outputs[0].Filename));

        sw.Stop();
        _logger.LogInformation("AI 生图完成：模板 {Template}，种子 {Seed}，耗时 {Ms}ms，输出 {Url}",
            req.Template, seed, sw.ElapsedMilliseconds, url);
        if (jobId.Length > 0) _tracker.Complete(jobId, url);

        return new AiImageResult { Url = url, Seed = seed, Width = width, Height = height, ElapsedMs = sw.ElapsedMilliseconds };
    }

    /// <summary>读取模板文件为可改写的 JSON 图。</summary>
    private static JsonNode LoadTemplate(string path)
    {
        var text = File.ReadAllText(path);
        var node = JsonNode.Parse(text);
        return node is not JsonObject
            ? throw new Exception($"模板 {Path.GetFileName(path)} 不是合法的 API 格式工作流（根节点应为对象）")
            : node;
    }

    /// <summary>
    /// 按 class_type 打补丁，返回（生效种子, 宽, 高）。
    /// 只改输入值，不动节点连接与拓扑——用户模板里接的放大、ControlNet、
    /// 保存节点都会原样生效。
    /// </summary>
    private static (long seed, int width, int height) PatchGraph(JsonNode graph, AiImageRequest req, string? uploadedImage)
    {
        if (graph is not JsonObject root) throw new Exception("工作流图结构异常");

        JsonObject? positive = null, negative = null, latent = null, sampler = null, loadImage = null;
        JsonObject? ckptLoader = null, unetLoader = null, imgScale = null;

        foreach (var (_, value) in root)
        {
            if (value is not JsonObject node) continue;
            var classType = node["class_type"]?.GetValue<string>() ?? "";
            var title = node["_meta"]?["title"]?.GetValue<string>() ?? "";

            switch (classType)
            {
                case "CLIPTextEncode":
                    // 标题/备注里带 negative/负面/负向 字样的判为负向；否则按出现顺序取第一个作正向
                    if (title.Contains("negative", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("负面") || title.Contains("负向"))
                        negative ??= node;
                    else
                        positive ??= node;
                    break;
                case "EmptyLatentImage":
                case "EmptySD3LatentImage":
                    latent ??= node; break;
                case "KSampler":
                case "KSamplerAdvanced":
                    sampler ??= node; break;
                case "LoadImage":
                    loadImage ??= node; break;
                case "CheckpointLoaderSimple":
                case "CheckpointLoader":
                    ckptLoader ??= node; break;
                case "UNETLoader":
                    unetLoader ??= node; break;
                case "ImageScale":
                    imgScale ??= node; break;
            }
        }
        // 标题没标 negative 的模板：第二个 CLIPTextEncode 按惯例就是负向
        if (negative == null)
        {
            var clips = root.Select(kv => kv.Value).OfType<JsonObject>()
                .Where(n => (n["class_type"]?.GetValue<string>() ?? "") == "CLIPTextEncode").ToList();
            if (clips.Count >= 2) negative = clips[1];
        }
        if (positive == null) throw new Exception("模板里找不到 CLIPTextEncode（提示词）节点，请检查导出的工作流");

        // 正向提示词：必填，直接覆写
        SetInput(positive, "text", req.Prompt.Trim());
        // 负向：模板里确有负向节点、且用户填了才覆写，留空保留模板原稿
        // （很多模板负向写得很讲究）；模板没有负向节点时不能拿正向节点顶替
        if (negative != null && !string.IsNullOrWhiteSpace(req.NegativePrompt))
            SetInput(negative, "text", req.NegativePrompt.Trim());

        // 尺寸：ComfyUI 要求 8 的倍数，向下取整兜底；同时记住最终宽高供回显。
        // 文生图改 EmptyLatentImage / EmptySD3LatentImage；图生图没有潜空间节点，
        // 改 ImageScale——参考图先缩放到目标尺寸再进 VAE，4GB 显存下降档主要靠这里。
        var finalW = 0; var finalH = 0;
        if (req.Width > 0) finalW = Floor8(req.Width);
        if (req.Height > 0) finalH = Floor8(req.Height);
        if (latent != null)
        {
            if (finalW > 0) SetInput(latent, "width", finalW);
            if (finalH > 0) SetInput(latent, "height", finalH);
        }
        if (imgScale != null)
        {
            if (finalW > 0) SetInput(imgScale, "width", finalW);
            if (finalH > 0) SetInput(imgScale, "height", finalH);
        }
        // 页面没填尺寸时回读模板原值，结果里给的宽高才是真实值
        if (finalW == 0 || finalH == 0)
        {
            if ((latent ?? imgScale)?["inputs"] is JsonObject sizeNode)
            {
                if (finalW == 0) finalW = ReadInt(sizeNode["width"]);
                if (finalH == 0) finalH = ReadInt(sizeNode["height"]);
            }
        }

        if (sampler != null)
        {
            if (req.Steps > 0) SetInput(sampler, "steps", req.Steps);
            if (req.Cfg > 0) SetInput(sampler, "cfg", (decimal)req.Cfg);
            if (req.Denoise > 0) SetInput(sampler, "denoise", (decimal)req.Denoise);
            // seed：-1 = 随机；固定值则写死，保证可复现
            var seed = req.Seed >= 0 ? req.Seed : Random.Shared.NextInt64(0, 2_000_000_000);
            SetInput(sampler, "seed", seed);
            SetInput(sampler, "noise_seed", seed); // KSamplerAdvanced 用 noise_seed 字段
        }

        if (!string.IsNullOrWhiteSpace(req.Model))
        {
            if (ckptLoader != null) SetInput(ckptLoader, "ckpt_name", req.Model);
            if (unetLoader != null) SetInput(unetLoader, "unet_name", req.Model);
        }

        if (loadImage != null && !string.IsNullOrEmpty(uploadedImage))
            SetInput(loadImage, "image", uploadedImage);

        // 返回生效种子供回显；没配 KSampler 时返回随机值占位
        if (sampler?["inputs"] is JsonObject si &&
            si.TryGetPropertyValue("seed", out var seedNode) &&
            seedNode is JsonValue sv && sv.TryGetValue(out long s))
            return (s, finalW, finalH);
        return (Random.Shared.NextInt64(0, 2_000_000_000), finalW, finalH);
    }

    /// <summary>
    /// ① 模板格式体检（同步、无网络）：确认是 Save (API Format) 导出的工作流。
    /// UI 格式导出带 nodes/links 数组，或节点带 widgets_values/pos 等界面字段；
    /// 合法 API 格式的根就是 { 节点id: { class_type, inputs } }。
    /// </summary>
    private static void ValidateTemplateShape(JsonNode graphNode)
    {
        if (graphNode is not JsonObject graph) throw new Exception("工作流图结构异常");

        if (graph.ContainsKey("nodes") || graph.ContainsKey("links"))
            throw new Exception("模板不是 API 格式工作流：请在 ComfyUI 里用菜单 Save → Save (API Format) 重新导出，再放进 back/ComfyWorkflows/ 目录");
        foreach (var (key, value) in graph)
        {
            if (value is not JsonObject node) continue;
            if (node.ContainsKey("widgets_values") || node.ContainsKey("pos") || node.ContainsKey("size"))
                throw new Exception($"模板不是 API 格式工作流（节点 {key} 带 widgets_values/pos 等界面字段）：请用 Save (API Format) 重新导出");
            if (node["class_type"] is null)
                throw new Exception($"模板节点 {key} 缺少 class_type，不是合法的 API 格式工作流");
        }
    }

    /// <summary>
    /// ② 节点注册检查：模板里每个 class_type 都要能在 ComfyUI 的 object_info 里查到，
    /// 否则是缺自定义节点（ComfyUI 对这种情况同样 400）。查不到 class_type 时报中文提示；
    /// object_info 端点整体不可用（老版本 ComfyUI）时静默跳过。
    /// </summary>
    private async Task CheckNodeTypesAsync(JsonObject graph, Dictionary<string, JsonElement?> infos, CancellationToken ct)
    {
        if (await _comfy.GetNodeInfoAsync("KSampler", ct) is null) return;

        var missing = new List<string>();
        foreach (var (key, value) in graph)
        {
            if (value is not JsonObject node) continue;
            var classType = node["class_type"]?.GetValue<string>() ?? "";
            if (classType.Length == 0) continue;
            if (!infos.TryGetValue(classType, out var spec))
            {
                spec = await _comfy.GetNodeInfoAsync(classType, ct);
                infos[classType] = spec;
            }
            if (spec is null) missing.Add($"{classType}（节点 {key}）");
        }
        if (missing.Count > 0)
            throw new Exception($"工作流里有未注册的节点类型：{string.Join("、", missing)}。" +
                "这些是 ComfyUI 自定义节点，请先在 ComfyUI Manager 中安装对应插件并重启 ComfyUI");
    }

    /// <summary>
    /// ③ 输入与取值预检（打补丁之后）：对照 object_info 的 required 定义，把 ComfyUI
    /// 会在 400 里抱怨的问题提前用中文说清楚——必填输入缺失、下拉类输入取值不在
    /// 可选列表（模型文件名写错/没下载，ComfyUI 报 value_not_in_list，最常见 400）。
    /// 与 <see cref="CheckNodeTypesAsync"/> 共用 object_info 缓存，不重复请求。
    /// </summary>
    private async Task PreflightAsync(JsonObject graph, Dictionary<string, JsonElement?> infos,
        HashSet<string>? trustedFiles, CancellationToken ct)
    {
        var problems = new List<string>();

        foreach (var (key, value) in graph)
        {
            if (value is not JsonObject node) continue;
            var classType = node["class_type"]?.GetValue<string>() ?? "";
            if (classType.Length == 0) continue;

            if (!infos.TryGetValue(classType, out var spec))
            {
                spec = await _comfy.GetNodeInfoAsync(classType, ct);
                infos[classType] = spec;
            }
            if (spec is null ||
                !spec.Value.TryGetProperty("input", out var input) ||
                !input.TryGetProperty("required", out var required) ||
                required.ValueKind != JsonValueKind.Object)
                continue; // 未注册节点已由 CheckNodeTypesAsync 报过；无输入定义的直接放过

            var inputs = node["inputs"] as JsonObject;
            foreach (var prop in required.EnumerateObject())
            {
                var field = prop.Name;
                var fieldSpec = prop.Value;
                // 必填输入缺失（连线输入在 API 格式里也是 ["节点", 槽位] 占位，缺 key 即真缺）
                if (inputs?.ContainsKey(field) is not true)
                {
                    problems.Add($"节点 {key}（{classType}）缺少必填输入 {field}");
                    continue;
                }
                // 下拉类（首项为字符串数组）：取值必须在列表里
                if (fieldSpec.ValueKind != JsonValueKind.Array || fieldSpec.GetArrayLength() == 0 ||
                    fieldSpec[0].ValueKind != JsonValueKind.Array)
                    continue;
                var options = fieldSpec[0].EnumerateArray()
                    .Where(o => o.ValueKind == JsonValueKind.String)
                    .Select(o => o.GetString() ?? "").ToList();
                var raw = inputs[field]?.ToJsonString();
                if (raw is null || !raw.StartsWith('"')) continue; // 连线/数字不查
                var actual = JsonSerializer.Deserialize<string>(raw);
                if (actual is null || options.Contains(actual)) continue;
                if (trustedFiles != null && trustedFiles.Contains(actual)) continue; // 本站刚上传、ComfyUI 已接收

                // LoadImage 的 image 取值不在列表：图生图模式（有上传）时上面已覆写+放行，
                // 走到这里说明是文生图模式选到了图生图模板——说清楚「切模式/换模板」，
                // 而不是让用户去 models 目录「下载模型」（参考图在 ComfyUI 的 input 目录）
                var isLoadImage = trustedFiles is null && classType == "LoadImage" && field == "image";
                problems.Add(options.Count == 0
                    ? isLoadImage
                        ? $"节点 {key}（LoadImage）的 image：ComfyUI 的 input 目录里没有可用的参考图——该模板需要参考图，请切换到「图生图」模式并上传参考图，或换用文生图模板"
                        : $"节点 {key}（{classType}）的 {field}：可选列表为空——对应模型目录里没有文件，请先下载模型（或用 ComfyUI Model Manager 下载）"
                    : isLoadImage
                        ? $"节点 {key}（LoadImage）的 image 取值「{actual}」在 ComfyUI 的 input 目录里不存在——该模板需要参考图，请切换到「图生图」模式并上传参考图，或换用文生图模板"
                        : $"节点 {key}（{classType}）的 {field} 取值「{actual}」不存在，当前可选 {options.Count} 个：" +
                          string.Join("、", options.Count <= 8 ? options : options.Take(8)) +
                          (options.Count > 8 ? " 等" : ""));
            }
        }

        if (problems.Count > 0)
            throw new Exception("工作流校验未通过：\n· " + string.Join("\n· ", problems) +
                "\n排查：模型类问题把文件放进 ComfyUI 对应 models 子目录并重启 ComfyUI；节点类问题安装自定义节点；详见 back/ComfyWorkflows/README.txt");
    }

    private static void SetInput(JsonObject node, string field, JsonNode? value)
    {
        if (node["inputs"] is not JsonObject inputs)
        {
            inputs = new JsonObject();
            node["inputs"] = inputs;
        }
        inputs[field] = value;
    }

    private static int Floor8(int v) => Math.Max(64, v / 8 * 8);

    private static string InferExt(string filename)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".webp" ? ext : ".png";
    }
}

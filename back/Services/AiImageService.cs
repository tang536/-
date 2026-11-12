using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ERental.DTOs;
using ERental.Options;
using Microsoft.Extensions.Options;

namespace ERental.Services;

///<summary>AI 生图服务：把用户在页面里填的参数，「打补丁」到他自己的 ComfyUI 工作流模板上</summary>
public class AiImageService(ComfyUiClient comfy,ImageStorage images,IOptions<AiImageOptions> opt,AiProgressTracker tracker,ILogger<AiImageService> logger)
{
    private readonly ComfyUiClient _comfy=comfy;
    private readonly ImageStorage _images=images;
    private readonly AiProgressTracker _tracker=tracker;
    private readonly ILogger<AiImageService> _logger=logger;

    private readonly AiImageOptions _opt=opt.Value;

    //ComfyUI 工作流 JSON 的字段名
    private const string ComfyFieldClassType="class_type";
    private const string ComfyFieldInputs="inputs";
    private const string ComfyFieldImage="image";
    private const string ComfyFieldNoiseSeed="noise_seed";
    private const string ComfyFieldClipTextEncode="CLIPTextEncode";
    private const string ComfyFieldWidth="width";
    private const string ComfyFieldHeight="height";

    ///<summary>模板图结构不合法时的统一提示（前端会把它直接展示给用户）</summary>
    private const string MsgWorkflowShapeError="工作流图结构异常";

    ///<summary>模板目录绝对路径（不存在则返回配置值拼出的路径，由调用方判在否）</summary>
    private string TemplatesDir=>Path.IsPathRooted(_opt.WorkflowsDir)?_opt.WorkflowsDir:Path.Combine(Directory.GetCurrentDirectory(),_opt.WorkflowsDir);

    ///<summary>可用模板名</summary>
    public List<string> ListTemplates()
    {
        var dir=TemplatesDir;
        if(!Directory.Exists(dir)) return[];
        return[.. Directory.GetFiles(dir,"*.json").Select(f=>Path.GetFileNameWithoutExtension(f)).OrderBy(n=>n,StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<AiImageStatus> GetStatusAsync(CancellationToken ct=default)
    {
        var status=new AiImageStatus
        {
            Templates=ListTemplates(),
            Online=await _comfy.IsOnlineAsync(ct)
        };
        if(!status.Online)
        {
            status.Message=$"ComfyUI 未启动或地址不通（{await _comfy.EffectiveBaseUrlAsync(ct)}）。"+"请先打开 Comfy Desktop 并等待界面加载完成；地址变更可在「系统配置」页更新。";
            return status;
        }
        //模型下拉：CheckpointLoaderSimple（safetensors 主模型）与 UNETLoader（GGUF）各拉一份
        status.Checkpoints=await _comfy.GetModelChoicesAsync("CheckpointLoaderSimple","ckpt_name",ct);
        status.DiffusionModels=await _comfy.GetModelChoicesAsync("UNETLoader","unet_name",ct);
        return status;
    }

    ///<summary>生成模板的表单 schema：遍历模板里的每个节点</summary>
    public async Task<AiTemplateSchema> BuildSchemaAsync(string template,CancellationToken ct=default)
    {
        var templatePath=ResolveTemplatePath(template);

        var graphNode=LoadTemplate(templatePath);
        ValidateTemplateShape(graphNode);
        if(graphNode is not JsonObject graph) throw new BusinessException(MsgWorkflowShapeError);

        var fields=new List<AiFieldDef>();
        var anyInfo=false;//object_info 是否至少可用一次
        var emptyCombos=0;//有字段定义但下拉为空的个数（模型目录为空；参考图不算）
        var promptRequiredDone=false;//首个提示词节点已标必填（负向/多区提示词不标）

        //单个节点的解析整体下沉到 ScanNodeAsync，这里只剩「遍历节点+汇总计数」
        foreach(var(key,value) in graph)
        {
            if(value is not JsonObject node) continue;
            var scanned=await ScanNodeAsync(key,node,promptRequiredDone,ct);
            anyInfo |= scanned.HasInputDef;
            emptyCombos+=scanned.EmptyCombos;
            promptRequiredDone |= scanned.PromptMarked;
            fields.AddRange(scanned.Fields);
        }

        var schema=new AiTemplateSchema{Template=template,Fields=SortFields(fields)};
        if(!anyInfo) schema.Notice="ComfyUI 离线或 object_info 不可用：下拉选项为空。可先按模板默认值填写，启动 ComfyUI 后刷新面板即可看到可选项。";
        else if(emptyCombos>0) schema.Notice=$"有 {emptyCombos} 个下拉选项为空——对应模型目录里还没有文件。可先去 ComfyUI Model Manager 下载模型，或使用其他已就绪的参数。";
        return schema;
    }

    ///<summary>模板名 → 磁盘路径：schema 与生成两个入口共用</summary>
    private string ResolveTemplatePath(string template)
    {
        if(string.IsNullOrWhiteSpace(template)) throw new BusinessException("请选择工作流模板");
        var templatePath=Path.Combine(TemplatesDir,template+".json");
        if(!File.Exists(templatePath)) throw new BusinessException($"模板不存在：{template}.json（应放在 back/ComfyWorkflows/ 目录）");
        return templatePath;
    }

    ///<summary>解析单个节点：产出它暴露给前端的字段，并报告本节点对全局计数的贡献</summary>
    private async Task<(List<AiFieldDef> Fields,bool HasInputDef,int EmptyCombos,bool PromptMarked)> ScanNodeAsync(
        string key,JsonObject node,bool promptRequiredDone,CancellationToken ct)
    {
        var classType=ClassTypeOf(node);
        if(!IsFieldBearingNode(classType)) return(new List<AiFieldDef>(),false,0,false);

        var spec=await _comfy.GetNodeInfoAsync(classType,ct);
        //未注册节点不暴露（提交前 CheckNodeTypesAsync 会报缺插件）
        if(spec is null||!spec.Value.TryGetProperty("input",out var inputDef)) return(new List<AiFieldDef>(),false,0,false);

        var title=node["_meta"]?["title"]?.GetValue<string>()??"";
        var inputs=node[ComfyFieldInputs] as JsonObject;
        var scanned=ScanSections(key,classType,title,inputs,inputDef,promptRequiredDone);
        return(scanned.Fields,true,scanned.EmptyCombos,scanned.PromptMarked);
    }

    ///<summary>按 required/optional 两段扫完一个节点的全部输入字段（鉴权/缓存等前置判断已由调用方做过）</summary>
    private static(List<AiFieldDef> Fields,int EmptyCombos,bool PromptMarked) ScanSections(
        string key,string classType,string title,JsonObject? inputs,JsonElement inputDef,bool promptRequiredDone)
    {
        var fields=new List<AiFieldDef>();
        var emptyCombos=0;
        var promptMarked=false;

        foreach(var section in new[]{"required","optional"})
        {
            if(!TryGetObjectSection(inputDef,section,out var sectionDef)) continue;

            foreach(var prop in sectionDef.EnumerateObject())
            {
                //传「是否还允许标必填」而不是只传 promptRequiredDone
                var built=TryBuildFieldDef(key,classType,title,prop,inputs,!promptRequiredDone&&!promptMarked);
                if(built is null) continue;
                fields.Add(built.Value.Def);
                promptMarked |= built.Value.PromptMarked;
                emptyCombos+=CountEmptyCombo(built.Value.Def);
            }
        }

        return(fields,emptyCombos,promptMarked);
    }

    ///<summary>取 object_info 里的 required/optional 段：不是对象说明该段缺失或结构异常</summary>
    private static bool TryGetObjectSection(JsonElement inputDef,string section,out JsonElement sectionDef)
        => inputDef.TryGetProperty(section,out sectionDef)&&sectionDef.ValueKind==JsonValueKind.Object;

    ///<summary>装饰节点（MarkdownNote/Reroute/Note）没有输入定义</summary>
    private static bool IsFieldBearingNode(string classType)
        => classType.Length>0&&classType is not("MarkdownNote" or "Reroute" or "Note");

    ///<summary>把一个 object_info 字段定义翻成前端字段</summary>
    private static(AiFieldDef Def,bool PromptMarked)? TryBuildFieldDef(
        string key,string classType,string title,JsonProperty prop,JsonObject? inputs,bool allowPromptRequired)
    {
        var field=prop.Name;
        var fieldSpec=prop.Value;
        if(fieldSpec.ValueKind!=JsonValueKind.Array||fieldSpec.GetArrayLength()==0) return null;
        if(!IsUserEditableInput(fieldSpec[0])) return null;

        //模板里的当前值作为默认值
        var current=inputs?[field];
        if(current is JsonArray) return null;

        var def=new AiFieldDef
        {
            Key=$"{key}.{field}",
            Label=BuildLabel(title,classType,field),
            Group=GroupOf(classType,field),
            NodeType=classType,
            InputName=field,
            Default=ReadDefaultValue(current)
        };

        if(fieldSpec[0].ValueKind==JsonValueKind.Array)
        {
            //下拉：第一项是候选值数组（模型清单/lora清单/采样器名……）
            def.Kind="select";
            def.Options=ReadComboOptions(fieldSpec);
        }
        else if(!TryApplyScalarKind(def,fieldSpec,field))
        {
            //类型名不在 INT/FLOAT/STRING/BOOLEAN 之内
            return null;
        }

        return(def,MarkPromptRequired(def,classType,field,allowPromptRequired));
    }

    ///<summary>模板里的当前值作为默认值：按 JSON 原始类型取，取不出就留空（null=不预填）</summary>
    private static object? ReadDefaultValue(JsonNode? current)=>current switch
    {
        JsonValue jv when jv.TryGetValue(out string? s)=>s,
        JsonValue jv when jv.TryGetValue(out bool b)=>b,
        JsonValue jv when jv.TryGetValue(out long l)=>l,
        JsonValue jv when jv.TryGetValue(out double d)=>d,
        _=>null
    };

    ///<summary>连线型输入（["MODEL"]/["CONDITIONING"]/["LATENT"]……）由工作流连线决定</summary>
    private static bool IsUserEditableInput(JsonElement first)
    {
        if(first.ValueKind==JsonValueKind.Array) return true;
        var t0=first.GetString()??"";
        return t0 is "INT" or "FLOAT" or "STRING" or "BOOLEAN";
    }

    ///<summary>按 object_info 的类型名定控件种类</summary>
    private static bool TryApplyScalarKind(AiFieldDef def,JsonElement fieldSpec,string field)
    {
        var typeName=fieldSpec[0].GetString()??"";
        var opts=ReadExtraOptions(fieldSpec);
        switch(typeName)
        {
            case "INT":
                def.Kind=field is "seed" or ComfyFieldNoiseSeed?"seed":"int";
                break;
            case "FLOAT":
                def.Kind="float";
                break;
            case "STRING":
                def.Kind=IsMultilineInput(opts)?"textarea":"text";
                break;
            case "BOOLEAN":
                def.Kind="boolean";
                def.Options=["true","false"];
                break;
            default:
                return false;
        }
        ApplyNumericRange(def,opts);
        return true;
    }

    ///<summary>第 3 段是节点的「额外配置」对象（min/max/step 等），可能不存在</summary>
    private static JsonElement? ReadExtraOptions(JsonElement fieldSpec)
        => fieldSpec.GetArrayLength() > 1&&fieldSpec[1].ValueKind==JsonValueKind.Object?fieldSpec[1]:null;

    ///<summary>STRING 输入的额外配置里声明 multiline 时，前端给多行文本域</summary>
    private static bool IsMultilineInput(JsonElement? opts)
        => opts?.TryGetProperty("multiline",out var ml)==true&&ml.ValueKind==JsonValueKind.True;

    ///<summary>min/max/step 只在确实是数字时采用</summary>
    private static void ApplyNumericRange(AiFieldDef def,JsonElement? opts)
    {
        if(opts is not{} o) return;
        if(o.TryGetProperty("min",out var mn)&&mn.ValueKind==JsonValueKind.Number) def.Min=mn.GetDouble();
        if(o.TryGetProperty("max",out var mx)&&mx.ValueKind==JsonValueKind.Number) def.Max=mx.GetDouble();
        if(o.TryGetProperty("step",out var st)&&st.ValueKind==JsonValueKind.Number) def.Step=st.GetDouble();
    }

    ///<summary>提示词是唯一必填项，且只标第一个文本节点（正向提示词）</summary>
    private static bool MarkPromptRequired(AiFieldDef def,string classType,string field,bool allow)
    {
        if(!allow||classType!=ComfyFieldClipTextEncode||field!="text") return false;
        def.Required=true;
        return true;
    }

    ///<summary>下拉为空=模型目录里没有文件（参考图节点不算），用来汇总顶部提示</summary>
    private static int CountEmptyCombo(AiFieldDef def)
        => def.Kind=="select"&&def.Options.Count==0&&def.Group!=ComfyFieldImage?1:0;

    ///<summary>分组排序：提示词 → 模型 → 尺寸 → 采样 → 图片 → 输出 → 其他</summary>
    private static List<AiFieldDef> SortFields(List<AiFieldDef> fields)
    {
        var order=new[]{"prompt","model","size","sampler",ComfyFieldImage,"output","other"};
        return[.. fields.OrderBy(f=>Array.IndexOf(order,f.Group)).ThenBy(f=>f.Key,StringComparer.Ordinal)];
    }

    ///<summary>节点类名（缺失时给空串）</summary>
    private static string ClassTypeOf(JsonObject node)=>node[ComfyFieldClassType]?.GetValue<string>()??"";

    ///<summary>图里的节点对象序列，跳过非对象值</summary>
    private static IEnumerable<JsonObject> EnumerateNodes(JsonObject root)=>root.Select(kv=>kv.Value).OfType<JsonObject>();

    ///<summary>字段标签</summary>
    private static string BuildLabel(string title,string classType,string field)
    {
        var baseName=!string.IsNullOrWhiteSpace(title)&&title!=classType?title:classType;
        return $"{baseName} · {field}";
    }

    ///<summary>按输入名归类（下拉按类名前缀归一化），前端按组分区渲染</summary>
    private static string GroupOf(string classType,string field)
    {
        var f=field.ToLowerInvariant();
        if(classType==ComfyFieldClipTextEncode&&f=="text") return "prompt";
        if(f is "ckpt_name" or "unet_name" or "unet_name_high_noise" or "unet_name_low_noise" or "gguf_name" or "vae_name" or "clip_name" or "clip_name1" or "clip_name2" or "clip_name3" or "clip_name4" or "clip_vision_name" or "lora_name" or "style_model_name" or "control_net_name" or "gligen_name" or "photomaker_name" or "model_name" or "diffusion_model_name" or "weight_dtype" or "device" or "type") return "model";
        if(f is ComfyFieldWidth or ComfyFieldHeight) return "size";
        if(f is "seed" or ComfyFieldNoiseSeed or "steps" or "cfg" or "denoise" or "sampler_name" or "scheduler" or "sigma_max" or "sigma_min" or "shift") return "sampler";
        if(f is ComfyFieldImage or "image1" or "image2") return ComfyFieldImage;
        return f is "filename_prefix"?"output":"other";
    }

    ///<summary>图里是否存在指定 class_type 的节点</summary>
    private static bool HasNodeType(JsonObject graph,string classType)
    {
        foreach(var(_,value) in graph)
        {
            if(value is JsonObject n&&(n[ComfyFieldClassType]?.GetValue<string>()??"")==classType) return true;
        }
        return false;
    }

    ///<summary>找第一个指定 class_type 的节点（同类多个时取图里顺序第一个）</summary>
    private static JsonObject? FindNode(JsonObject graph,string classType)
    {
        foreach(var(_,value) in graph)
        {
            if(value is JsonObject n&&(n[ComfyFieldClassType]?.GetValue<string>()??"")==classType) return n;
        }
        return null;
    }

    ///<summary>通用补丁通道：按 schema 的「节点id</summary>
    private static void ApplyInputs(JsonObject root,Dictionary<string,object?> inputs)
    {
        foreach(var(key,value) in inputs)
        {
            if(value is null) continue;
            var dot=key.IndexOf('.');
            if(dot<=0||dot==key.Length-1) continue;
            if(root[key[..dot]] is not JsonObject node) continue;
            if(node[ComfyFieldInputs] is not JsonObject ins) continue;
            var field=key[(dot+1)..];
            var current=ins[field];
            if(current is JsonArray) continue;//连线输入不可覆写
            var converted=ConvertToJsonValue(value,current);
            if(converted!=null) ins[field]=converted;
        }
    }

    ///<summary>把页面传来的字符串/数字/JsonElement 转成与模板输入类型一致的 JSON 值</summary>
    private static JsonNode? ConvertToJsonValue(object? value,JsonNode? current)
    {
        var s=ToInvariantString(value);
        if(string.IsNullOrWhiteSpace(s)) return null;

        //两步：先按模板现值定型（能保住 int 槽位不被写成浮点），定不出类型再按字面推断
        return TryCoerceWithCurrentType(s,current)??InferByLiteral(s);
    }

    ///<summary>把任意入参统一成字符串形式</summary>
    private static string? ToInvariantString(object? value)=>value switch
    {
        null=>null,
        string str=>str,
        bool b=>b?"true":"false",
        double d=>d.ToString(CultureInfo.InvariantCulture),
        float f=>f.ToString(CultureInfo.InvariantCulture),
        int or long or decimal=>value.ToString(),
        JsonElement je=>FromJsonElement(je),
        _=>value.ToString()
    };

    ///<summary>JsonElement 的单值化：只有字符串/布尔/数字能变成输入值</summary>
    private static string? FromJsonElement(JsonElement je)=>je.ValueKind switch
    {
        JsonValueKind.String=>je.GetString(),
        JsonValueKind.True=>"true",
        JsonValueKind.False=>"false",
        JsonValueKind.Number=>je.ToString(),
        JsonValueKind.Undefined=>null,
        JsonValueKind.Object=>null,
        JsonValueKind.Array=>null,
        JsonValueKind.Null=>null,
        _=>null                      //未命名枚举值（JsonValueKind 是开放枚举）同样到底
    };

    ///<summary>模板该输入已有值时，按它的类型解释用户输入</summary>
    private static JsonNode? TryCoerceWithCurrentType(string s,JsonNode? current)
    {
        if(current is not JsonValue cv) return null;
        if(cv.TryGetValue(out string? _)) return s;
        if(cv.TryGetValue(out bool cb)) return bool.TryParse(s,out var b)?b:cb;
        if(WantsDouble(s,cv)) return double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out var d)?d:cv;
        if(cv.TryGetValue(out long _)) return long.TryParse(s,out var l)?l:cv;
        if(cv.TryGetValue(out double _)) return double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out var dAny)?dAny:cv;
        return null;
    }

    ///<summary>「带小数点」或模板现值是浮点的一律走 double</summary>
    private static bool WantsDouble(string s,JsonValue cv)
        => s.Contains('.')||s.Contains('e')||s.Contains('E')||(cv.TryGetValue(out double cd0)&&Math.Abs(cd0%1) > double.Epsilon);

    ///<summary>模板里没有该输入时按字面推断：整数</summary>
    private static JsonNode? InferByLiteral(string s)
    {
        if(long.TryParse(s,out var l2)) return l2;
        if(double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out var d2)) return d2;
        if(bool.TryParse(s,out var b2)) return b2;
        return s;
    }

    ///<summary>种子填 -1=随机：覆写后统一替换所有采样器的 seed/noise_seed</summary>
    private static void RandomizeSeedIfRequested(JsonObject root)
    {
        foreach(var(_,value) in root)
        {
            if(value is not JsonObject node) continue;
            if(node[ComfyFieldInputs] is not JsonObject ins) continue;
            foreach(var field in new[]{"seed",ComfyFieldNoiseSeed})
            {
                if(ins[field] is JsonValue v&&v.TryGetValue(out long sv)&&sv<0) ins[field]=Random.Shared.NextInt64(0,2_000_000_000);
            }
        }
    }

    ///<summary>补丁后回读（生效种子,宽,高）供结果回显</summary>
    private static(long seed,int width,int height) ReadBackPatchResults(JsonObject root)
    {
        var seed=Random.Shared.NextInt64(0,2_000_000_000);
        var width=0;
        var height=0;
        JsonObject? sampler=null,sizeNode=null;
        foreach(var(_,value) in root)
        {
            if(value is not JsonObject node) continue;
            switch(node[ComfyFieldClassType]?.GetValue<string>()??"")
            {
                case "KSampler":
                case "KSamplerAdvanced": sampler ??= node;break;
                case "EmptyLatentImage":
                case "EmptySD3LatentImage":
                case "ImageScale": sizeNode ??= node;break;
            }
        }
        if(sampler?[ComfyFieldInputs] is JsonObject si)
        {
            if(si["seed"] is JsonValue s1&&s1.TryGetValue(out long v1)) seed=v1;
            else if(si[ComfyFieldNoiseSeed] is JsonValue s2&&s2.TryGetValue(out long v2)) seed=v2;
        }
        if(sizeNode?[ComfyFieldInputs] is JsonObject zi)
        {
            width=ReadInt(zi[ComfyFieldWidth]);
            height=ReadInt(zi[ComfyFieldHeight]);
        }
        return(seed,width,height);
    }

    ///<summary>从 JSON 值读整数（补丁写入的可能是 long/double，逐个尝试）</summary>
    private static int ReadInt(JsonNode? node)
    {
        if(node is JsonValue v)
        {
            if(v.TryGetValue(out long l)) return(int)l;
            if(v.TryGetValue(out int i)) return i;
            if(v.TryGetValue(out double d)) return(int)d;
        }
        return 0;
    }

    ///<summary>生成入口</summary>
    public async Task<AiImageResult> GenerateAsync(AiImageRequest req,Stream? imgStream,string? imgFileName,CancellationToken ct=default)
    {
        var templatePath=ResolveTemplatePath(req.Template);

        var sw=Stopwatch.StartNew();

        //图生图：先把参考图传进 ComfyUI 的 input 目录，拿到服务端文件名
        var uploadedName=await UploadReferenceAsync(imgStream,imgFileName,ct);

        //载入模板 → ① 格式体检 → ② 节点注册检查 → ③ 打补丁 → ④ 输入与取值预检 → 提交顺序有意为之
        var graphNode=LoadTemplate(templatePath);
        ValidateTemplateShape(graphNode);
        if(graphNode is not JsonObject graph) throw new BusinessException(MsgWorkflowShapeError);
        //模板里确实有提示词节点时才要求填提示词（纯视频类工作流没有 CLIPTextEncode）
        if(string.IsNullOrWhiteSpace(req.Prompt)&&HasNodeType(graph,ComfyFieldClipTextEncode)) throw new BusinessException("请填写提示词");

        var infos=new Dictionary<string,JsonElement?>(StringComparer.Ordinal);
        await CheckNodeTypesAsync(graph,infos,ct);

        //③ 打补丁：两条通道的选择与细节都在 ApplyPatch 里
        var(seed,width,height)=ApplyPatch(graph,req,uploadedName);

        //trustedFiles
        var trusted=string.IsNullOrEmpty(uploadedName)?null:new HashSet<string>{uploadedName};
        await PreflightAsync(graph,infos,trusted,ct);

        //提交并等待执行完成：首选 ComfyUI WebSocket 实时进度（排队/节点/采样步数/失败原因）
        var jobId=(req.JobId??"").Trim();
        if(jobId.Length>0) _tracker.Begin(jobId);

        //执行+取输出：失败时要把跟踪器置为失败再抛出，这层 try/catch 单独成段更容易看住
        var outputs=await ExecuteAndCollectOutputsAsync(graph,jobId,CreateProgress(jobId),ct);

        //下载第一张成品并落盘到 uploads/ai/
        var url=await SaveOutputImageAsync(outputs,ct);

        sw.Stop();
        _logger.LogInformation("AI 生图完成：模板 {Template}，种子 {Seed}，耗时 {Ms}ms，输出 {Url}",
            req.Template,seed,sw.ElapsedMilliseconds,url);
        if(jobId.Length>0) _tracker.Complete(jobId,url);

        return new AiImageResult{Url=url,Seed=seed,Width=width,Height=height,ElapsedMs=sw.ElapsedMilliseconds};
    }

    ///<summary>图生图上传参考图</summary>
    private async Task<string?> UploadReferenceAsync(Stream? imgStream,string? imgFileName,CancellationToken ct)
    {
        if(imgStream is null) return null;
        var fileName=string.IsNullOrWhiteSpace(imgFileName)?"reference.png":imgFileName;
        return await _comfy.UploadImageAsync(imgStream,fileName,ct);
    }

    ///<summary>③ 打补丁：schema 驱动（Inputs 非空）走通用通道按「节点id.输入名」覆写</summary>
    private static(long seed,int width,int height) ApplyPatch(JsonObject graph,AiImageRequest req,string? uploadedName)
    {
        var inputs=req.Inputs;
        if(inputs is not{Count: > 0}) return PatchGraph(graph,req,uploadedName);

        ApplyInputs(graph,inputs!);
        //图生图：上传的参考图覆写 LoadImage（无论 Inputs 里有没有带它）
        SetImageIfPresent(FindNode(graph,"LoadImage"),uploadedName);
        //种子填 -1=随机：统一在覆写后替换，保证可复现语义对所有采样器生效
        RandomizeSeedIfRequested(graph);
        return ReadBackPatchResults(graph);
    }

    ///<summary>参考图覆写 LoadImage：只在「有上传」且「模板里确有该节点」时生效</summary>
    private static void SetImageIfPresent(JsonObject? loadImageNode,string? uploadedName)
    {
        if(!string.IsNullOrEmpty(uploadedName)&&loadImageNode is not null) SetInput(loadImageNode,ComfyFieldImage,uploadedName);
    }

    ///<summary>提交并等待执行完成，成功后返回输出图片清单</summary>
    private async Task<List<ComfyOutputImage>> ExecuteAndCollectOutputsAsync(
        JsonObject graph,string jobId,IProgress<ComfyProgress>? progress,CancellationToken ct)
    {
        try
        {
            var promptId=await _comfy.ExecutePromptAsync(
                graph.ToJsonString(),progress,TimeSpan.FromSeconds(Math.Max(30,_opt.TimeoutSeconds)),ct);
            //执行成功后再查一次历史拿输出图片清单（此时历史里必定有）
            var outputs=await _comfy.GetOutputImagesAsync(promptId,ct);
            if(outputs.Count==0)
                throw new BusinessException("ComfyUI 报告执行成功，但历史里没有输出图片（可重试一次）");
            return outputs;
        }
        catch(Exception ex)
        {
            if(jobId.Length>0) _tracker.Fail(jobId,ex.Message);
            throw;
        }
    }

    ///<summary>前端进度回调：没有 jobId 时前端不会轮询，不必建 Progress</summary>
    private Progress<ComfyProgress>? CreateProgress(string jobId)
        => jobId.Length>0?new Progress<ComfyProgress>(p=>_tracker.Report(jobId,p.Percent,p.Stage)):null;

    ///<summary>下载首张成品并落盘，返回可访问 URL</summary>
    private async Task<string> SaveOutputImageAsync(List<ComfyOutputImage> outputs,CancellationToken ct)
    {
        var bytes=await _comfy.GetImageAsync(outputs[0],ct);
        if(bytes.Length==0) throw new BusinessException("ComfyUI 返回了空图片");
        await using var ms=new MemoryStream(bytes);
        return await _images.SaveStreamAsync(ms,"ai",InferExt(outputs[0].Filename));
    }

    ///<summary>读取模板文件为可改写的 JSON 图</summary>
    private static JsonNode LoadTemplate(string path)
    {
        var text=File.ReadAllText(path);
        var node=JsonNode.Parse(text);
        return node is not JsonObject?throw new BusinessException($"模板 {Path.GetFileName(path)} 不是合法的 API 格式工作流（根节点应为对象）"):node;
    }

    ///<summary>按 class_type 打补丁，返回（生效种子,宽,高）</summary>
    private static(long seed,int width,int height) PatchGraph(JsonNode graph,AiImageRequest req,string? uploadedImage)
    {
        if(graph is not JsonObject root) throw new BusinessException(MsgWorkflowShapeError);

        var targets=FindPatchTargets(root);
        //标题没标 negative 的模板：第二个 CLIPTextEncode 按惯例就是负向
        var negative=targets.Negative??FindSecondClipNode(root);
        var positive=targets.Positive??throw new BusinessException("模板里找不到 CLIPTextEncode（提示词）节点，请检查导出的工作流");

        //四段补丁各管一类输入
        ApplyPromptPatch(positive,negative,req);
        var(finalW,finalH)=ApplySizePatch(targets.Latent,targets.ImageScale,req);
        var seed=ApplySamplerPatch(targets.Sampler,req);
        ApplyModelPatch(targets.CheckpointLoader,targets.UnetLoader,req.Model);
        SetImageIfPresent(targets.LoadImage,uploadedImage);

        //返回生效种子供回显
        return(seed??Random.Shared.NextInt64(0,2_000_000_000),finalW,finalH);
    }

    ///<summary>一次遍历收集补丁要改的节点（同类多个时取图里顺序第一个）</summary>
    private static(JsonObject? Positive,JsonObject? Negative,JsonObject? Latent,JsonObject? Sampler,
        JsonObject? LoadImage,JsonObject? CheckpointLoader,JsonObject? UnetLoader,JsonObject? ImageScale) FindPatchTargets(JsonObject root)
    {
        JsonObject? positive=null,negative=null,latent=null,sampler=null,loadImage=null;
        JsonObject? ckptLoader=null,unetLoader=null,imgScale=null;

        foreach(var(_,value) in root)
        {
            if(value is not JsonObject node) continue;
            switch(ClassTypeOf(node))
            {
                case ComfyFieldClipTextEncode:
                    AssignClipNode(node,ref positive,ref negative);
                    break;
                case "EmptyLatentImage":
                case "EmptySD3LatentImage":
                    TakeNodeIfAbsent(ref latent,node);break;
                case "KSampler":
                case "KSamplerAdvanced":
                    TakeNodeIfAbsent(ref sampler,node);break;
                case "LoadImage":
                    TakeNodeIfAbsent(ref loadImage,node);break;
                case "CheckpointLoaderSimple":
                case "CheckpointLoader":
                    TakeNodeIfAbsent(ref ckptLoader,node);break;
                case "UNETLoader":
                    TakeNodeIfAbsent(ref unetLoader,node);break;
                case "ImageScale":
                    TakeNodeIfAbsent(ref imgScale,node);break;
            }
        }

        return(positive,negative,latent,sampler,loadImage,ckptLoader,unetLoader,imgScale);
    }

    ///<summary>同类节点取图里第一个：后面的同类型节点不覆盖已选中的那个</summary>
    private static void TakeNodeIfAbsent(ref JsonObject? slot,JsonObject node)=>slot ??= node;

    ///<summary>标题/备注里带 negative/负面/负向 字样的判为负向</summary>
    private static void AssignClipNode(JsonObject node,ref JsonObject? positive,ref JsonObject? negative)
    {
        var title=node["_meta"]?["title"]?.GetValue<string>()??"";
        if(title.Contains("negative",StringComparison.OrdinalIgnoreCase)||title.Contains("负面")||title.Contains("负向")) negative ??= node;
        else positive ??= node;
    }

    ///<summary>没被标题标成负向时，图里第二个 CLIPTextEncode 按惯例当作负向</summary>
    private static JsonObject? FindSecondClipNode(JsonObject root)
    {
        var clips=EnumerateNodes(root).Where(n=>ClassTypeOf(n)==ComfyFieldClipTextEncode).ToList();
        return clips.Count>=2?clips[1]:null;
    }

    ///<summary>正向提示词必填、直接覆写</summary>
    private static void ApplyPromptPatch(JsonObject positive,JsonObject? negative,AiImageRequest req)
    {
        SetInput(positive,"text",req.Prompt.Trim());
        //（很多模板负向写得很讲究）
        if(negative is not null&&!string.IsNullOrWhiteSpace(req.NegativePrompt)) SetInput(negative,"text",req.NegativePrompt.Trim());
    }

    ///<summary>尺寸：ComfyUI 要求 8 的倍数，向下取整兜底</summary>
    private static(int Width,int Height) ApplySizePatch(JsonObject? latent,JsonObject? imgScale,AiImageRequest req)
    {
        //文生图改 EmptyLatentImage/EmptySD3LatentImage
        var finalW=0;
        var finalH=0;
        if(req.Width is > 0) finalW=Floor8(req.Width.Value);
        if(req.Height is > 0) finalH=Floor8(req.Height.Value);
        WriteDimension(latent,finalW,finalH);
        WriteDimension(imgScale,finalW,finalH);

        if(finalW!=0&&finalH!=0) return(finalW,finalH);
        //页面没填尺寸时回读模板原值，结果里给的宽高才是真实值
        return ReadMissingSize((latent??imgScale)?[ComfyFieldInputs] as JsonObject,finalW,finalH);
    }

    ///<summary>写宽高：0 表示用户没填，保留模板原值不覆盖</summary>
    private static void WriteDimension(JsonObject? node,int width,int height)
    {
        if(node is null) return;
        if(width>0) SetInput(node,ComfyFieldWidth,width);
        if(height>0) SetInput(node,ComfyFieldHeight,height);
    }

    ///<summary>把没填的那一维从模板里补回来（填了的维度不覆盖）</summary>
    private static(int Width,int Height) ReadMissingSize(JsonObject? sizeNode,int width,int height)
    {
        if(sizeNode is null) return(width,height);
        if(width==0) width=ReadInt(sizeNode[ComfyFieldWidth]);
        if(height==0) height=ReadInt(sizeNode[ComfyFieldHeight]);
        return(width,height);
    }

    ///<summary>采样参数：步数/CFG/降噪只在用户填了才覆盖，seed 一定写</summary>
    private static long? ApplySamplerPatch(JsonObject? sampler,AiImageRequest req)
    {
        if(sampler is null) return null;
        if(req.Steps is > 0) SetInput(sampler,"steps",req.Steps.Value);
        if(req.Cfg is > 0) SetInput(sampler,"cfg",(decimal)req.Cfg.Value);
        if(req.Denoise is > 0) SetInput(sampler,"denoise",(decimal)req.Denoise.Value);
        //seed：-1=随机
        var seed=req.Seed>=0?req.Seed:Random.Shared.NextInt64(0,2_000_000_000);
        SetInput(sampler,"seed",seed);
        SetInput(sampler,ComfyFieldNoiseSeed,seed);
        return seed;
    }

    ///<summary>模型名同时覆盖 ckpt 与 unet 两类加载器（模板用哪个就改哪个）</summary>
    private static void ApplyModelPatch(JsonObject? ckptLoader,JsonObject? unetLoader,string? model)
    {
        if(string.IsNullOrWhiteSpace(model)) return;
        if(ckptLoader is not null) SetInput(ckptLoader,"ckpt_name",model);
        if(unetLoader is not null) SetInput(unetLoader,"unet_name",model);
    }

    ///<summary>① 模板格式体检（同步、无网络）：确认是 Save(API Format) 导出的工作流</summary>
    private static void ValidateTemplateShape(JsonNode graphNode)
    {
        if(graphNode is not JsonObject graph) throw new BusinessException(MsgWorkflowShapeError);

        if(graph.ContainsKey("nodes")||graph.ContainsKey("links")) throw new BusinessException("模板不是 API 格式工作流：请在 ComfyUI 里用菜单 Save → Save (API Format) 重新导出，再放进 back/ComfyWorkflows/ 目录");
        foreach(var(key,value) in graph)
        {
            if(value is not JsonObject node) continue;
            if(node.ContainsKey("widgets_values")||node.ContainsKey("pos")||node.ContainsKey("size")) throw new BusinessException($"模板不是 API 格式工作流（节点 {key} 带 widgets_values/pos 等界面字段）：请用 Save (API Format) 重新导出");
            if(node[ComfyFieldClassType] is null) throw new BusinessException($"模板节点 {key} 缺少 class_type，不是合法的 API 格式工作流");
        }
    }

    ///<summary>② 节点注册检查：模板里每个 class_type 都要能在 ComfyUI 的 object_info 里查到</summary>
    private async Task CheckNodeTypesAsync(JsonObject graph,Dictionary<string,JsonElement?> infos,CancellationToken ct)
    {
        if(await _comfy.GetNodeInfoAsync("KSampler",ct) is null) return;

        var missing=new List<string>();
        foreach(var(key,value) in graph)
        {
            if(value is not JsonObject node) continue;
            var classType=node[ComfyFieldClassType]?.GetValue<string>()??"";
            if(classType.Length==0) continue;
            if(!infos.TryGetValue(classType,out var spec))
            {
                spec=await _comfy.GetNodeInfoAsync(classType,ct);
                infos[classType]=spec;
            }
            if(spec is null) missing.Add($"{classType}（节点 {key}）");
        }
        if(missing.Count>0)
            throw new BusinessException($"工作流里有未注册的节点类型：{string.Join("、", missing)}。" +
                "这些是 ComfyUI 自定义节点，请先在 ComfyUI Manager 中安装对应插件并重启 ComfyUI");
    }

    ///<summary>③ 输入与取值预检（打补丁之后）：对照 object_info 的 required 定义</summary>
    private async Task PreflightAsync(JsonObject graph,Dictionary<string,JsonElement?> infos,
        HashSet<string>? trustedFiles,CancellationToken ct)
    {
        var problems=new List<string>();

        //逐节点收集问题而不是「查到第一个就抛」：用户一次能看全所有缺项，少来回改
        foreach(var(key,value) in graph)
        {
            if(value is not JsonObject node) continue;
            problems.AddRange(await CheckNodeInputsAsync(key,node,infos,trustedFiles,ct));
        }

        if(problems.Count>0) throw new BusinessException("工作流校验未通过：\n· "+string.Join("\n· ",problems)+"\n排查：模型类问题把文件放进 ComfyUI 对应 models 子目录并重启 ComfyUI；节点类问题安装自定义节点；详见 back/ComfyWorkflows/README.txt");
    }

    ///<summary>检查单个节点的必填输入，返回该节点的全部问题（空=通过）</summary>
    private async Task<List<string>> CheckNodeInputsAsync(string key,JsonObject node,Dictionary<string,JsonElement?> infos,
        HashSet<string>? trustedFiles,CancellationToken ct)
    {
        var problems=new List<string>();
        var classType=ClassTypeOf(node);
        if(classType.Length==0) return problems;

        var spec=await GetNodeInfoCachedAsync(classType,infos,ct);
        //未注册节点已由 CheckNodeTypesAsync 报过
        if(!TryGetRequiredInputs(spec,out var required)) return problems;

        var inputs=node[ComfyFieldInputs] as JsonObject;
        foreach(var prop in required.EnumerateObject())
        {
            var problem=CheckRequiredInput(key,classType,inputs,prop,trustedFiles);
            if(problem is not null) problems.Add(problem);
        }
        return problems;
    }

    ///<summary>object_info 结果按 class_type 缓存</summary>
    private async Task<JsonElement?> GetNodeInfoCachedAsync(string classType,Dictionary<string,JsonElement?> infos,CancellationToken ct)
    {
        if(infos.TryGetValue(classType,out var cached)) return cached;
        var spec=await _comfy.GetNodeInfoAsync(classType,ct);
        infos[classType]=spec;
        return spec;
    }

    ///<summary>取 object_info 的 input.required 段（缺失或非对象都视为「无需预检」）</summary>
    private static bool TryGetRequiredInputs(JsonElement? spec,out JsonElement required)
    {
        required=default;
        if(spec is null||!spec.Value.TryGetProperty("input",out var input)) return false;
        if(!input.TryGetProperty("required",out var req)||req.ValueKind!=JsonValueKind.Object) return false;
        required=req;
        return true;
    }

    ///<summary>校验一个必填输入：缺失、或下拉取值不在列表里都算问题</summary>
    private static string? CheckRequiredInput(string key,string classType,JsonObject? inputs,JsonProperty prop,HashSet<string>? trustedFiles)
    {
        var field=prop.Name;
        //必填输入缺失（连线输入在 API 格式里也是["节点",槽位] 占位，缺 key 即真缺）
        if(inputs?.ContainsKey(field) is not true) return $"节点 {key}（{classType}）缺少必填输入 {field}";

        //只有下拉类（首项为字符串数组）才需要比对取值
        var fieldSpec=prop.Value;
        if(!IsComboInputDef(fieldSpec)) return null;

        var options=ReadComboOptions(fieldSpec);
        var actual=ReadStringLiteral(inputs[field]);
        if(actual is null||options.Contains(actual)) return null;
        if(trustedFiles?.Contains(actual) is true) return null;//本站刚上传、ComfyUI 已接收

        return BuildMissingChoiceProblem(key,classType,field,actual,options,trustedFiles);
    }

    ///<summary>下拉定义的判定：首元素必须是字符串数组（连线占位["节点",槽位] 首元素是数字）</summary>
    private static bool IsComboInputDef(JsonElement fieldSpec)
        => fieldSpec.ValueKind==JsonValueKind.Array&&fieldSpec.GetArrayLength() > 0&&fieldSpec[0].ValueKind==JsonValueKind.Array;

    ///<summary>展开下拉候选值（第一项是候选数组，非字符串的忽略）</summary>
    private static List<string> ReadComboOptions(JsonElement fieldSpec)
        => [.. fieldSpec[0].EnumerateArray().Where(o=>o.ValueKind==JsonValueKind.String).Select(o=>o.GetString()??"")];

    ///<summary>只有字符串字面量才需要比对候选列表</summary>
    private static string? ReadStringLiteral(JsonNode? value)
    {
        var raw=value?.ToJsonString();
        if(raw is null||!raw.StartsWith('"')) return null;
        return JsonSerializer.Deserialize<string>(raw);
    }

    ///<summary>「取值不在候选列表」的错误文案：按「候选是否为空 × 是否参考图节点」四种组合分别措辞</summary>
    private static string BuildMissingChoiceProblem(string key,string classType,string field,string actual,
        List<string> options,HashSet<string>? trustedFiles)
    {
        //LoadImage 的 image 取值不在列表：图生图模式（有上传）时上面已覆写+放行
        var isLoadImage=trustedFiles is null&&classType=="LoadImage"&&field==ComfyFieldImage;

        //四种组合用元组 switch 一次列全，比嵌套三元可读
        return(options.Count==0,isLoadImage) switch
        {
            (true,true)=>$"节点 {key}（LoadImage）的 image：ComfyUI 的 input 目录里没有可用的参考图——该模板需要参考图，请切换到「图生图」模式并上传参考图，或换用文生图模板",
            (true,false)=>$"节点 {key}（{classType}）的 {field}：可选列表为空——对应模型目录里没有文件，请先下载模型（或用 ComfyUI Model Manager 下载）",
            (false,true)=>$"节点 {key}（LoadImage）的 image 取值「{actual}」在 ComfyUI 的 input 目录里不存在——该模板需要参考图，请切换到「图生图」模式并上传参考图，或换用文生图模板",
            _=>$"节点 {key}（{classType}）的 {field} 取值「{actual}」不存在，当前可选 {options.Count} 个："+string.Join("、",options.Count<=8?options:options.Take(8))+(options.Count>8?" 等":"")
        };
    }

    private static void SetInput(JsonObject node,string field,JsonNode? value)
    {
        if(node[ComfyFieldInputs] is not JsonObject inputs)
        {
            inputs=new JsonObject();
            node[ComfyFieldInputs]=inputs;
        }
        inputs[field]=value;
    }

    private static int Floor8(int v)=>Math.Max(64,v/8*8);

    private static string InferExt(string filename)
    {
        var ext=Path.GetExtension(filename).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".webp"?ext:".png";
    }
}

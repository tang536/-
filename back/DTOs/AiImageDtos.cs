namespace ERental.DTOs;

/// <summary>AI 生图请求（文生图 / 图生图共用）。</summary>
public class AiImageRequest
{
    /// <summary>模板名（ComfyWorkflows 目录下的 .json 文件名，不含扩展名）。</summary>
    public string Template { get; init; } = string.Empty;

    /// <summary>正向提示词（写进第一个 CLIPTextEncode 节点）。</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>负向提示词（写进 negative CLIPTextEncode 节点；留空则不动模板原值）。</summary>
    public string? NegativePrompt { get; init; }

    /// <summary>宽度（EmptyLatentImage.width；0 = 用模板原值）。</summary>
    public int Width { get; init; }

    /// <summary>高度（EmptyLatentImage.height；0 = 用模板原值）。</summary>
    public int Height { get; init; }

    /// <summary>采样步数（KSampler.steps；0 = 用模板原值）。</summary>
    public int Steps { get; init; }

    /// <summary>提示词遵循度（KSampler.cfg；0 = 用模板原值）。</summary>
    public double Cfg { get; init; }

    /// <summary>重绘幅度（KSampler.denoise；0 = 用模板原值）。图生图常用 0.4~0.8。</summary>
    public double Denoise { get; init; }

    /// <summary>随机种子；-1 = 每次随机，>=0 = 固定种子（便于复现同一张图）。</summary>
    public long Seed { get; init; } = -1;

    /// <summary>主模型文件名（CheckpointLoaderSimple.ckpt_name / UNETLoader.unet_name；留空用模板原值）。</summary>
    public string? Model { get; init; }

    /// <summary>
    /// 动态字段值字典（schema 驱动）：key = "{节点id}.{输入名}"（来自
    /// <see cref="AiTemplateSchema.Fields"/> 的 <see cref="AiFieldDef.Key"/>），
    /// value 为字符串/数字。本字典非空时，补丁只按它覆写，忽略上面的固定字段
    /// （固定字段保留给不拉 schema 的简单调用方兜底）。
    /// </summary>
    public Dictionary<string, object?>? Inputs { get; set; }

    /// <summary>
    /// 任务编号（前端生成的 UUID）。带上它才能凭 jobId 轮询实时进度
    /// （<c>GET /api/ai-image/progress/{jobId}</c>，页面进度条的数据源）；
    /// 不带则只返回最终结果，无进度显示。
    /// </summary>
    public string? JobId { get; init; }
}

/// <summary>AI 生图结果。</summary>
public class AiImageResult
{
    /// <summary>成品图相对路径（/uploads/ai/xxx.png），可直接 &lt;img src&gt;。</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>本次实际使用的种子（前端回显，方便复现）。</summary>
    public long Seed { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>总耗时毫秒（排队 + 推理 + 下载）。</summary>
    public long ElapsedMs { get; init; }
}

/// <summary>ComfyUI 连接状态 + 可用资源（前端下拉据此渲染）。</summary>
public class AiImageStatus
{
    /// <summary>ComfyUI 是否在线（桌面版是否已启动）。</summary>
    public bool Online { get; init; }

    /// <summary>可用工作流模板名列表。</summary>
    public List<string> Templates { get; init; } = [];

    /// <summary>ComfyUI 报告的错误（离线原因等）。</summary>
    public string? Message { get; set; }

    /// <summary>可选主模型（CheckpointLoaderSimple 的 ckpt_name 候选）。</summary>
    public List<string> Checkpoints { get; set; } = [];

    /// <summary>可选主模型（UNETLoader 的 unet_name 候选，GGUF 量化模型走这个节点）。</summary>
    public List<string> DiffusionModels { get; set; } = [];
}

/// <summary>
/// 模板的一个可配置字段——按模板实际节点 + ComfyUI object_info 实时定义生成，
/// 与节点 id 无关、与工作流复杂程度无关：用户工作流里有几个可填的输入，
/// 表单就渲染几项（模型、LoRA、步数、CFG、宽高、多区提示词、shift、batch……）。
/// </summary>
public class AiFieldDef
{
    /// <summary>补丁键："{节点id}.{输入名}"，提交时原样带回 Inputs 字典。</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>字段标签（优先用节点标题，如「正向提示词 · text」）。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>分组：prompt / model / sampler / size / image / output / other。</summary>
    public string Group { get; init; } = "other";

    /// <summary>控件类型：text / textarea / int / float / select / seed / boolean。</summary>
    public string Kind { get; set; } = "text";

    /// <summary>select/boolean 的可选项（来自 object_info 下拉定义；离线时可能为空）。</summary>
    public List<string> Options { get; set; } = [];

    /// <summary>模板里的当前值（前端作为默认值/占位提示）。</summary>
    public object? Default { get; set; }

    /// <summary>数值范围（object_info 的 min/max/step，没有则 null）。</summary>
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }

    /// <summary>是否必填（提示词类字段；其余留空即用模板原值）。</summary>
    public bool Required { get; set; }

    /// <summary>节点 class_type（排错时展示，如 UNETLoader）。</summary>
    public string NodeType { get; init; } = string.Empty;

    /// <summary>输入名（如 unet_name / steps / text）。</summary>
    public string InputName { get; init; } = string.Empty;
}

/// <summary>模板的表单 schema：选中模板后前端据此动态渲染整张表单。</summary>
public class AiTemplateSchema
{
    public string Template { get; init; } = string.Empty;

    /// <summary>可配置字段清单（按分组排序，同组内按节点 id 稳定排序）。</summary>
    public List<AiFieldDef> Fields { get; init; } = [];

    /// <summary>ComfyUI 离线等原因拿不到下拉选项时的说明（不影响填写）。</summary>
    public string? Notice { get; set; }
}

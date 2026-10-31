namespace ERental.Options;

/// <summary>
/// AI 生图（ComfyUI）接入参数。
/// 本机跑的是「Comfy Desktop」桌面版（安装目录
/// C:\Users\&lt;用户&gt;\AppData\Local\Comfy-Desktop\ComfyUI-Installs\ComfyUI，
/// 共享 input/output 与 models 目录在 ComfyUI-Shared 下）。桌面版自带一个
/// ComfyUI 服务，默认监听 127.0.0.1:8188，本系统所有出站请求都打这个地址。
/// </summary>
public class AiImageOptions
{
    /// <summary>ComfyUI 服务地址（桌面版默认 127.0.0.1:8188）。</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8188";

    /// <summary>
    /// 工作流模板目录（相对 ContentRoot，默认 back/ComfyWorkflows）。
    /// 用户从桌面版里「Save (API Format)」导出的 .json 就放在这里，
    /// 文件名（不含扩展名）即前端下拉里的模板名。
    /// </summary>
    public string WorkflowsDir { get; set; } = "ComfyWorkflows";

    /// <summary>轮询队列/历史的间隔毫秒。</summary>
    public int PollIntervalMs { get; set; } = 1200;

    /// <summary>单次生成的总体超时秒数（排队 + 推理 + 下载一并计入）。</summary>
    public int TimeoutSeconds { get; set; } = 600;
}

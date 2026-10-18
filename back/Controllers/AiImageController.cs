using ERental.DTOs;
using ERental.Models;
using ERental.Services;
using ERental.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ERental.Controllers;

/// <summary>
/// AI 生图：对接本机 ComfyUI 桌面版（默认 127.0.0.1:8188）。
/// 页面填提示词/尺寸/种子，后端把参数打补丁到用户自己的工作流模板
/// （back/ComfyWorkflows/*.json，从桌面版 Save (API Format) 导出），
/// 提交执行并下载成品图。
///
/// 角色：仅管理员。生图结果只用于车辆图片，属于用况4 车辆档案维护范畴。
/// </summary>
[ApiController]
// 显式写连字符路由：[controller] 令牌会生成「AiImage」，与前端约定的
// /api/ai-image/... 差一个连字符，路由匹配大小写不敏感但符号敏感，会 404。
[Route("api/ai-image")]
[Authorize(Policy = Policies.AdminOnly)]
public class AiImageController(AiImageService svc, AiProgressTracker tracker, ILogger<AiImageController> logger) : ControllerBase
{
    private readonly AiImageService _svc = svc;
    private readonly AiProgressTracker _tracker = tracker;
    private readonly ILogger<AiImageController> _logger = logger;

    /// <summary>
    /// 连接状态 + 可用资源（模板下拉 / 模型下拉 / ComfyUI 是否在线）。
    /// 前端打开 AI 面板时先调这个，离线时直接提示「请先启动 Comfy Desktop」。
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var status = await _svc.GetStatusAsync(ct);
        return Ok(new ApiResponse<AiImageStatus> { Data = status });
    }

    /// <summary>可用模板列表（单独一个轻量接口，轮询状态时不必拉模型清单）。</summary>
    [HttpGet("templates")]
    public IActionResult Templates()
    {
        return Ok(new ApiResponse<List<string>> { Data = _svc.ListTemplates() });
    }

    /// <summary>
    /// 模板表单 schema：选中模板后前端据此动态渲染整张表单——模板里有哪些可填的
    /// 输入（模型/LoRA/步数/CFG/宽高/多区提示词/shift……）就渲染哪些控件，
    /// 选项与取值范围来自 ComfyUI 实时的 object_info，与节点 id 无关。
    /// </summary>
    [HttpGet("schema")]
    public async Task<IActionResult> Schema([FromQuery] string template, CancellationToken ct)
    {
        try
        {
            var schema = await _svc.BuildSchemaAsync(template, ct);
            return Ok(new ApiResponse<AiTemplateSchema> { Data = schema });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI schema 生成失败：{Template}", template);
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false });
        }
    }

    /// <summary>
    /// 生成进度轮询：前端点「开始生成」时带上前台生成的 jobId，之后每秒轮询一次，
    /// 按返回的 Percent / Stage 渲染进度条；State 为 done（Message = 成品图 URL）
    /// 或 error（Message = 失败原因）时停止。未登记的任务（如后端刚重启）返回
    /// Data = null，前端继续等 POST 的最终结果即可，不影响主流程。
    /// </summary>
    [HttpGet("progress/{jobId}")]
    public IActionResult Progress(string jobId)
    {
        var state = _tracker.Get(jobId);
        return Ok(new ApiResponse<AiProgressState?> { Data = state });
    }

    /// <summary>文生图。</summary>
    [HttpPost("text2img")]
    public async Task<IActionResult> Text2Img([FromBody] AiImageRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _svc.GenerateAsync(req, null, null, ct);
            return Ok(new ApiResponse<AiImageResult> { Data = result, Message = "生成成功" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI 文生图失败：模板 {Template}", req.Template);
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false });
        }
    }

    /// <summary>
    /// 图生图：参考图以 multipart/form-data 上传（字段名 file），
    /// 其余参数与文生图同（JSON 字段直接平铺在 form 里，模型绑定自动转换）。
    /// 动态字段字典（inputs）以 inputsJson 文本字段传入，在此手工反序列化——
    /// multipart 无法直接把字典绑进 Dictionary&lt;string, object?&gt;。
    /// </summary>
    [HttpPost("img2img")]
    public async Task<IActionResult> Img2Img([FromForm] AiImageRequest req, IFormFile? file, CancellationToken ct)
    {
        try
        {
            if (file == null || file.Length == 0) throw new Exception("图生图需要上传参考图");
            var inputsJson = Request.Form["inputsJson"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(inputsJson))
                req.Inputs = JsonSerializer.Deserialize<Dictionary<string, object?>>(inputsJson);
            await using var stream = file.OpenReadStream();
            var result = await _svc.GenerateAsync(req, stream, file.FileName, ct);
            return Ok(new ApiResponse<AiImageResult> { Data = result, Message = "生成成功" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI 图生图失败：模板 {Template}", req.Template);
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false });
        }
    }
}

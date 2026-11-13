using ERental.DTOs;
using ERental.Models;
using ERental.Services;
using ERental.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ERental.Controllers;

///<summary>AI 生图：对接本机 ComfyUI 桌面版</summary>
[ApiController]
//显式写连字符路由：[controller] 令牌会生成「AiImage」
[Route("api/ai-image")]
[Authorize(Policy = Policies.AdminOnly)]
//S6960 建议拆成「状态/模板查询」与「生成」两个控制器
public class AiImageController(AiImageService svc, AiProgressTracker tracker, ILogger<AiImageController> logger) : ControllerBase //NOSONAR 理由见上：控制器按页面边界组织，拆分不降低耦合且须新增类型
{
    private readonly AiImageService _svc = svc;
    private readonly AiProgressTracker _tracker = tracker;
    private readonly ILogger<AiImageController> _logger = logger;

    ///<summary>连接状态+可用资源（模板下拉/模型下拉/ComfyUI 是否在线）</summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetAiServiceStatus(CancellationToken ct)
    {
        var status = await _svc.GetStatusAsync(ct);
        return Ok(new ApiResponse<AiImageStatus> { Data = status });
    }

    ///<summary>可用模板列表（单独一个轻量接口，轮询状态时不必拉模型清单）</summary>
    [HttpGet("templates")]
    [ProducesResponseType(typeof(ApiResponse<List<string>>), StatusCodes.Status200OK)]
    public IActionResult ListAiTemplates()
    {
        return Ok(new ApiResponse<List<string>> { Data = _svc.ListTemplates() });
    }

    ///<summary>模板表单 schema：选中模板后前端据此动态渲染整张表单</summary>
    [HttpGet("schema")]
    public async Task<IActionResult> GetWorkflowSchema([FromQuery] string template, CancellationToken ct)
    {
        try
        {
            var schema = await _svc.BuildSchemaAsync(template, ct);
            return Ok(new ApiResponse<AiTemplateSchema> { Data = schema });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI schema 生成失败：{Template}", template);
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false });
        }
    }

    ///<summary>生成进度轮询：前端点「开始生成」时带上前台生成的 jobId，之后每秒轮询一次</summary>
    [HttpGet("progress/{jobId}")]
    [ProducesResponseType(typeof(ApiResponse<AiProgressState?>), StatusCodes.Status200OK)]
    public IActionResult GetAiJobProgress(string jobId)
    {
        var state = _tracker.Get(jobId);
        return Ok(new ApiResponse<AiProgressState?> { Data = state });
    }

    ///<summary>文生图</summary>
    [HttpPost("text2img")]
    public async Task<IActionResult> GenerateImageByText([FromBody] AiImageRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _svc.GenerateAsync(req, null, null, ct);
            return Ok(new ApiResponse<AiImageResult> { Data = result, Message = "生成成功" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI 文生图失败：模板 {Template}", req.Template);
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false });
        }
    }

    ///<summary>图生图：参考图以 multipart/form-data 上传（字段名 file）</summary>
    [HttpPost("img2img")]
    public async Task<IActionResult> GenerateImageByImage([FromForm] AiImageRequest req, IFormFile? file, [FromForm] string? inputsJson, CancellationToken ct)
    {
        try
        {
            if (file == null || file.Length == 0) throw new BusinessException("图生图需要上传参考图");
            if (!string.IsNullOrWhiteSpace(inputsJson)) req.Inputs = JsonSerializer.Deserialize<Dictionary<string, object?>>(inputsJson);
            await using var stream = file.OpenReadStream();
            var result = await _svc.GenerateAsync(req, stream, file.FileName, ct);
            return Ok(new ApiResponse<AiImageResult> { Data = result, Message = "生成成功" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI 图生图失败：模板 {Template}", req.Template);
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false });
        }
    }
}

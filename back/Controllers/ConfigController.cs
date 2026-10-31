using System.Security.Claims;
using ERental.DTOs;
using ERental.Models;
using ERental.Services;
using ERental.Authorization;
using ERental.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERental.Controllers;

/// <summary>系统配置 + 数据字典管理，均仅管理员可用（需求 4</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = Policies.AdminOnly)]
public class ConfigController(
    SystemConfigService svc, ComfyConfigService comfyCfg, ComfyUiClient comfy,
    IHttpClientFactory httpFactory) : ControllerBase
{
    private readonly SystemConfigService _svc = svc;
    private readonly ComfyConfigService _comfyCfg = comfyCfg;
    private readonly ComfyUiClient _comfy = comfy;
    private readonly IHttpClientFactory _httpFactory = httpFactory;

    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    [HttpGet]
    public async Task<IActionResult> ManageSystemConfig() => Ok(new ApiResponse<object> { Data = await _svc.GetAll() });

    [HttpPut("{key}")]
    public async Task<IActionResult> UpdateSystemConfig(string key, [FromBody] string value)
    {
        try { await _svc.Update(key, value); return Ok(new ApiResponse<object> { Message = "配置更新成功" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>删除管理员自建的配置项（核心业务参数受保护，只能改值不能删）</summary>
    [HttpDelete("{key}")]
    public async Task<IActionResult> DeleteSystemConfig(string key)
    {
        try { await _svc.Delete(key); return Ok(new ApiResponse<object> { Message = "配置项已删除" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    // ==================== 数据字典（需求 4.2.1） ====================

    [HttpGet("dict")]
    public async Task<IActionResult> QueryDictItems() => Ok(new ApiResponse<object> { Data = await _svc.GetDict() });

    [HttpPost("dict")]
    public async Task<IActionResult> AddDictItem([FromBody] DictRequest req)
    {
        try { return Ok(new ApiResponse<object> { Data = await _svc.AddDict(req), Message = "字典项已添加" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    [HttpPut("dict/{id:int}")]
    public async Task<IActionResult> UpdateDictItem(int id, [FromBody] DictRequest req)
    {
        try { await _svc.UpdateDict(id, req); return Ok(new ApiResponse<object> { Message = "字典项已更新" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    [HttpDelete("dict/{id:int}")]
    public async Task<IActionResult> DeleteDictItem(int id)
    {
        try { await _svc.DeleteDict(id); return Ok(new ApiResponse<object> { Message = "字典项已删除" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>字典自更新：扫描车辆品牌、维保故障类型、会员权益等级，把库中实际值同步进字典</summary>
    [HttpPost("dict/sync")]
    public async Task<IActionResult> SyncDictItems()
    {
        try
        {
            var result = await _svc.SyncDictFromDbAsync(CurrentUserId);
            var added = result.Sum(r => r.Added);
            var detail = string.Join("、", result.Select(r => $"{r.GroupName}+{r.Added}"));
            return Ok(new ApiResponse<object>
            {
                Message = added > 0 ? $"同步完成，新增 {added} 项（{detail}）" : "数据库与字典已一致，无需更新",
                Data = result.Select(r => new { group = r.Group, groupName = r.GroupName, added = r.Added })
            });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    // ==================== ComfyUI 地址（AI 生图） ====================

    /// <summary>读取 ComfyUI 地址配置：当前生效值 + 来源（db = 页面配的 / appsettings = 出厂默认）…</summary>
    [HttpGet("comfyui")]
    public async Task<IActionResult> GetComfyConfig(CancellationToken ct)
    {
        var (url, source) = await _comfyCfg.GetEffectiveAsync(ct);
        var online = await _comfy.IsOnlineAsync(ct);
        return Ok(new ApiResponse<object>
        {
            Data = new { url, source, defaultUrl = _comfyCfg.DefaultUrl, online }
        });
    }

    /// <summary>保存 ComfyUI 地址：换端口 / 换机器后在这里更新，保存后即时生效</summary>
    [HttpPut("comfyui")]
    public async Task<IActionResult> SetComfyConfig([FromBody] ComfyUiUrlRequest req, CancellationToken ct)
    {
        try
        {
            var url = await _comfyCfg.SetAsync(req.Url, ct);
            return Ok(new ApiResponse<object> { Data = new { url }, Message = "ComfyUI 地址已保存，立即生效" });
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false });
        }
    }

    /// <summary>测试连接：不保存，直接探测填的地址通不通（给「先测再存」用）</summary>
    [HttpPost("comfyui/test")]
    [SkipAudit]   // 纯连通性探测，不改任何数据，记进操作日志只会淹没真正的配置变更
    public async Task<IActionResult> TestComfyConnection([FromBody] ComfyUiUrlRequest req, CancellationToken ct)
    {
        var url = req.Url.Trim();
        if (url.Length == 0)
            return BadRequest(new ApiResponse<object> { Code = 400, Message = "请先填写地址", Success = false });
        try
        {
            // 命名客户端 comfy-probe：绕开系统代理 + 6 秒超时（注册见 Program.cs）
            var client = _httpFactory.CreateClient("comfy-probe");
            var resp = await client.GetAsync(url.TrimEnd('/') + "/system_stats", ct);
            var ok = resp.IsSuccessStatusCode;
            return Ok(new ApiResponse<object>
            {
                Data = new { ok },
                Message = ok ? "连接成功：ComfyUI 在线" : $"ComfyUI 无响应（HTTP {(int)resp.StatusCode}）"
            });
        }
        catch (Exception ex)
        {
            // 连接失败也走 200 + ok=false：这是「测试结果」不是「接口错误」，
            // 前端要的是能展示的消息，不是错误码
            return Ok(new ApiResponse<object>
            {
                Data = new { ok = false },
                Message = $"连接失败：{ErrorText.Safe(ex)}"
            });
        }
    }

    /// <summary>数据库初始化：清空全部业务运行数据（订单/支付/结算/维保/盘点/报废/续租/日志/客户/车辆）</summary>
    [HttpPost("reset-db")]
    public async Task<IActionResult> ResetDatabase()
    {
        try
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            var (tables, rows) = await _svc.ResetDatabaseAsync(CurrentUserId, ip);
            return Ok(new ApiResponse<object> { Message = $"初始化完成：已清空 {tables} 张表（共 {rows} 行），管理员账号、系统配置与支付配置保留" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }
}

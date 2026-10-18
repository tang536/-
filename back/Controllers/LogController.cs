using System.Security.Claims;
using ERental.Models;
using ERental.Services;
using ERental.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERental.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = Policies.AdminOnly)]
public class LogController(LogService svc, BackupService backup) : ControllerBase
{
    private readonly LogService _svc = svc;
    private readonly BackupService _backup = backup;

    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>
    /// 操作日志查询（需求 4.2.1）：按用户、时间、操作类型（模块/结果）+ 内容关键字组合筛选。
    /// 日志只读——本控制器不提供任何删除或修改入口，保证「不可删除、不可篡改」。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        int page = 1, int size = 20, int? userId = null,
        DateTime? from = null, DateTime? to = null,
        string? module = null, string? result = null, string? keyword = null)
    {
        var (total, items) = await _svc.GetAll(page, size, userId, from, to, module, result, keyword);
        return Ok(new ApiResponse<object> { Data = new { Total = total, Page = page, Size = size, Items = items } });
    }

    /// <summary>日志里实际出现过的操作模块（筛选下拉选项，避免前端硬编码模块名）</summary>
    [HttpGet("modules")]
    public async Task<IActionResult> GetModules() =>
        Ok(new ApiResponse<List<string>> { Data = await _svc.GetModules() });

    /// <summary>
    /// 数据库备份（需求 4.2.1 / 6.4）：导出 17 张业务表，AES-256-GCM 加密后落盘。
    /// 返回结构与旧版保持一致（fileName / sizeKB / tableCount），前端无需改造。
    /// </summary>
    [HttpPost("backup")]
    public async Task<IActionResult> Backup()
    {
        try
        {
            var r = await _backup.CreateAsync();
            await _svc.WriteLog(CurrentUserId,
                $"执行数据库备份，生成加密文件 {r.FileName}（{r.SizeBytes / 1024}KB，{r.TableCount} 张表）",
                "系统管理", "成功", ClientIp);
            return Ok(new ApiResponse<object>
            {
                Message = $"数据库备份成功，共 {r.TableCount} 张表（已加密）",
                Data = new
                {
                    r.FileName,
                    SizeKB = Math.Round(r.SizeBytes / 1024.0, 2),
                    r.TableCount,
                    r.Encrypted
                }
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<object> { Code = 400, Message = $"备份失败：{ex.Message}", Success = false });
        }
    }

    /// <summary>备份文件列表（新的在前），供管理员下载或选择恢复点</summary>
    [HttpGet("backups")]
    public IActionResult GetBackups() =>
        Ok(new ApiResponse<object> { Data = _backup.ListFiles() });

    /// <summary>下载备份文件（加密后的二进制；解密密钥只在服务端，下载件泄露也读不出内容）</summary>
    [HttpGet("backups/{fileName}/download")]
    public async Task<IActionResult> Download(string fileName)
    {
        try
        {
            var (name, content) = await _backup.ReadAsync(fileName);
            await _svc.WriteLog(CurrentUserId, $"下载数据库备份 {name}", "系统管理", "成功", ClientIp);
            return File(content, "application/octet-stream", name);
        }
        catch (Exception ex)
        {
            return BadRequest(new ApiResponse<object> { Code = 400, Message = ex.Message, Success = false });
        }
    }

    /// <summary>
    /// 用备份恢复数据库（需求 6.4「一键恢复」）。
    ///
    /// 二次确认不是前端弹窗那种"软确认"——必须把 confirm 字段写成 RESTORE，
    /// 避免误点/脚本调用直接把生产数据覆盖掉。恢复前系统会自动做一份快照。
    /// </summary>
    [HttpPost("restore")]
    public async Task<IActionResult> Restore([FromBody] RestoreRequest req)
    {
        if (!string.Equals(req.Confirm, "RESTORE", StringComparison.Ordinal))
            return BadRequest(new ApiResponse<object>
            {
                Code = 400,
                Message = "危险操作：请在 confirm 字段填写 RESTORE 以确认覆盖当前数据库",
                Success = false
            });

        try
        {
            var r = await _backup.RestoreAsync(req.FileName);
            await _svc.WriteLog(CurrentUserId,
                $"从备份 {r.FileName} 恢复数据库（{r.TableCount} 张表 / {r.Rows} 行；恢复前快照 {r.SafetyBackup}）",
                "系统管理", "成功", ClientIp);
            return Ok(new ApiResponse<object>
            {
                Message = $"恢复完成：{r.Rows} 行数据；恢复前快照已保存为 {r.SafetyBackup}",
                Data = r
            });
        }
        catch (Exception ex)
        {
            await _svc.WriteLog(CurrentUserId, $"从备份 {req.FileName} 恢复失败：{ex.Message}",
                "系统管理", "失败", ClientIp);
            return BadRequest(new ApiResponse<object> { Code = 400, Message = $"恢复失败：{ex.Message}", Success = false });
        }
    }

    public sealed record RestoreRequest(string FileName, string? Confirm);
}

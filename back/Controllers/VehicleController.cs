using System.Security.Claims;
using ERental.DTOs;
using ERental.Models;
using ERental.Services;
using ERental.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERental.Controllers;

[ApiController]
[Route("api/[controller]")]
// ⚠️ 类级只写 [Authorize]：类级与方法级的 Roles 是**交集**（两条策略都要过），
// 类上写「管理员,前台」的话，方法级再怎么加「顾客」也放不进顾客。角色一律下沉到方法级。
// 读档案 = 管理员/前台（前台开单要选车）；增删改与状态强制变更属用况4，仅管理员。
[Authorize(Policy = Policies.AnyAuthenticated)]
public class VehicleController(VehicleService svc, AuthService auth) : ControllerBase
{
    private readonly VehicleService _svc = svc;
    private readonly AuthService _auth = auth;

    /// <summary>车辆台账</summary>
    [HttpGet]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> QueryVehicleInfo(string? status, string? keyword, string? brand, string? sortBy = null, bool sortDesc = true, int page = 0, int pageSize = 0)
    {
        if (pageSize > 0)
        {
            var (items, total) = await _svc.GetPaged(status, keyword, brand, sortBy, sortDesc, page, pageSize);
            var (p, size, _, _) = Paging.Normalize(page, pageSize);
            return Ok(new ApiResponse<PagedResult<VehicleDto>>
            {
                Data = new PagedResult<VehicleDto>(items.Select(VehicleDto.From).ToList(), total, p, size)
            });
        }
        return Ok(new ApiResponse<List<VehicleDto>>
        {
            Data = (await _svc.GetAll(status, keyword)).Select(VehicleDto.From).ToList()
        });
    }

    /// <summary>各状态车辆数（车辆页签角标）</summary>
    [HttpGet("stats")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> GetVehicleStats(string? keyword, string? brand) => Ok(new ApiResponse<Dictionary<string, int>> { Data = await _svc.GetStatusCountsAsync(keyword, brand) });

    [HttpGet("{id:int}")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> QueryVehicleInfoById(int id)
    {
        try
        {
            var vehicle = await _svc.GetById(id);
            return Ok(new ApiResponse<VehicleDto> { Data = VehicleDto.From(vehicle) });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>扫码查询（用况4 步骤3 / 需求 4</summary>
    [HttpGet("qr")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> GetVehicleByQrCode([FromQuery] string? code)
    {
        try { return Ok(new ApiResponse<VehicleQrDetailDto> { Data = await _svc.GetByQrCode(code) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>某辆车的租赁记录（车辆档案详情弹窗与二维码弹窗共用）</summary>
    [HttpGet("{id:int}/orders")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> GetVehicleOrderHistory(int id)
    {
        try { return Ok(new ApiResponse<List<VehicleOrderBriefDto>> { Data = await _svc.GetOrderHistory(id) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>最近热销车辆：顾客线上租车页的推荐位，故对顾客一并开放</summary>
    [HttpGet("popular")]
    [Authorize(Policy = Policies.AdminOrReceptionOrCustomer)]
    public async Task<IActionResult> GetPopularVehicles(int days = 30, int limit = 6) => Ok(new ApiResponse<List<PopularVehicleDto>> { Data = await _svc.GetPopular(days, limit) });

    /// <summary>顾客端「全部车辆」目录：热销板块点「查看全部车辆」后进入， 返回全部门店车辆（含在租/维保），字段与热销卡片一致但不…</summary>
    [HttpGet("catalog")]
    [Authorize(Policy = Policies.AdminOrReceptionOrCustomer)]
    public async Task<IActionResult> BrowseVehicleCatalog([FromQuery] string? keyword) => Ok(new ApiResponse<List<PopularVehicleDto>> { Data = await _svc.GetCatalog(keyword) });

    /// <summary>上传车辆图片（multipart/form-data，字段名 file），返回 { url }</summary>
    [HttpPost("{id:int}/image")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> UploadCarImage(int id, IFormFile? file)
    {
        try
        {
            var url = await _svc.SetImage(id, file);
            return Ok(new ApiResponse<object> { Data = new { url }, Message = "车辆图片已上传" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>直接以 URL 设置车辆图片（AI 生图结果落地用：生成成功后前端把 /uploads/ai/xxx</summary>
    [HttpPut("{id:int}/image-url")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> SetCarImageUrl(int id, [FromBody] SetVehicleImageUrlDto dto)
    {
        try
        {
            await _svc.SetImageUrl(id, dto.Url);
            return Ok(new ApiResponse<object> { Message = string.IsNullOrWhiteSpace(dto.Url) ? "车辆图片已清除" : "车辆图片已更新" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>清除车辆图片</summary>
    [HttpDelete("{id:int}/image")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> DeleteCarImage(int id)
    {
        try
        {
            await _svc.SetImage(id, null);
            return Ok(new ApiResponse<object> { Message = "车辆图片已清除" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    [HttpPost]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> AddCarInfo([FromBody] VehicleSaveDto dto)
    {
        try
        {
            var created = await _svc.Create(dto);
            return Ok(new ApiResponse<VehicleDto> { Data = VehicleDto.From(created) });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>批量新增车辆：逐条创建、逐条校验，失败项不影响其它项</summary>
    [HttpPost("batch")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> BatchAddCar([FromBody] List<VehicleSaveDto> vehicles)
    {
        try
        {
            var data = await _svc.CreateBatch(vehicles);
            return Ok(new ApiResponse<BatchVehicleResultDto>
            {
                Data = data,
                Message = data.SuccessCount == data.Total ? $"成功新增 {data.SuccessCount} 辆" : $"成功 {data.SuccessCount} / 共 {data.Total} 辆"
            });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>批量编辑车辆：仅覆盖被勾选的字段（Apply* 开关），未勾选保持原值</summary>
    [HttpPut("batch")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> BatchEditCar([FromBody] BatchVehicleUpdateDto dto)
    {
        try
        {
            var uid = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            // 状态变更需管理员密码二次校验（用况4 扩展用况3）：整批只校验一次，密码错则整批中止
            if (dto.ApplyStatus)
                await _auth.VerifyStaffPassword(uid, Request.Headers["X-Op-Password"].FirstOrDefault());
            var data = await _svc.UpdateBatch(dto, uid);
            return Ok(new ApiResponse<BatchVehicleResultDto>
            {
                Data = data,
                Message = data.SuccessCount == data.Total ? $"成功更新 {data.SuccessCount} 辆" : $"成功 {data.SuccessCount} / 共 {data.Total} 辆"
            });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>更新车辆档案</summary>
    [HttpPut("{id:int}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> EditCarInfo(int id, [FromBody] VehicleSaveDto dto)
    {
        try
        {
            var uid = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var current = await _svc.GetById(id);
            if (!string.Equals(current.VehicleStatus, dto.VehicleStatus, StringComparison.Ordinal))
                await _auth.VerifyStaffPassword(uid, Request.Headers["X-Op-Password"].FirstOrDefault());
            await _svc.Update(id, dto, uid);
            return Ok(new ApiResponse<object> { Message = "更新成功" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> DeleteCarInfo(int id)
    {
        try { await _svc.Delete(id); return Ok(new ApiResponse<object> { Message = "删除成功" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>手动更改车辆状态（用况4 步骤4 / 扩展用况3）</summary>
    [HttpPut("{id:int}/status")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> UpdateCarStatus(int id, [FromBody] ChangeVehicleStatusDto dto)
    {
        try
        {
            var uid = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await _auth.VerifyStaffPassword(uid, dto.Password);
            await _svc.UpdateStatus(id, dto.Status, uid);
            return Ok(new ApiResponse<object> { Message = "状态更新成功" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }
}

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

    /// <summary>
    /// 车辆台账。传 <c>pageSize&gt;0</c> 时返回分页结构 { items, total, page, pageSize }，
    /// 不传则维持原来的全量数组（顾客端选车、可用车辆等小结果集调用不受影响）。
    /// </summary>
    [HttpGet]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> GetAll(string? status, string? keyword, string? brand,
        string? sortBy = null, bool sortDesc = true,
        int page = 0, int pageSize = 0)
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

    /// <summary>各状态车辆数（车辆页签角标）。分页后前端没有全集，角标改由后端分组统计。</summary>
    [HttpGet("stats")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> GetStats(string? keyword, string? brand) =>
        Ok(new ApiResponse<Dictionary<string, int>> { Data = await _svc.GetStatusCountsAsync(keyword, brand) });

    [HttpGet("{id:int}")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> GetById(int id)
    {
        try
        {
            var vehicle = await _svc.GetById(id);
            return Ok(new ApiResponse<VehicleDto> { Data = VehicleDto.From(vehicle) });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>
    /// 扫码查询（用况4 步骤3 / 需求 4.2.2「扫码可快速查看车辆档案与租赁记录」）。
    /// 入参 code 可以是二维码原文 <c>ERENTAL:VEHICLE:V001</c>，也可以直接是车辆编号
    /// —— 需求 4.2.2 提到的 USB 扫码枪本质就是键盘输入，两种形式都要认。
    ///
    /// 参数刻意声明为 <c>string?</c>：非可空引用类型会被 [ApiController] 隐式加上 [Required]，
    /// 扫码枪读到空内容时会被模型验证拦在方法外面，返回一个没有 message 的 400，
    /// 用户看不到「无法识别」这句人话。
    /// </summary>
    [HttpGet("qr")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> GetByQrCode([FromQuery] string? code)
    {
        try { return Ok(new ApiResponse<VehicleQrDetailDto> { Data = await _svc.GetByQrCode(code) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>某辆车的租赁记录（车辆档案详情弹窗与二维码弹窗共用）</summary>
    [HttpGet("{id:int}/orders")]
    [Authorize(Policy = Policies.AdminOrReception)]
    public async Task<IActionResult> GetOrderHistory(int id)
    {
        try { return Ok(new ApiResponse<List<VehicleOrderBriefDto>> { Data = await _svc.GetOrderHistory(id) }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>最近热销车辆：顾客线上租车页的推荐位，故对顾客一并开放</summary>
    [HttpGet("popular")]
    [Authorize(Policy = Policies.AdminOrReceptionOrCustomer)]
    public async Task<IActionResult> GetPopular(int days = 30, int limit = 6) =>
        Ok(new ApiResponse<List<PopularVehicleDto>> { Data = await _svc.GetPopular(days, limit) });

    /// <summary>
    /// 顾客端「全部车辆」目录：热销板块点「查看全部车辆」后进入，
    /// 返回全部门店车辆（含在租/维保），字段与热销卡片一致但不含经营数据。
    /// </summary>
    [HttpGet("catalog")]
    [Authorize(Policy = Policies.AdminOrReceptionOrCustomer)]
    public async Task<IActionResult> GetCatalog([FromQuery] string? keyword) =>
        Ok(new ApiResponse<List<PopularVehicleDto>> { Data = await _svc.GetCatalog(keyword) });

    /// <summary>上传车辆图片（multipart/form-data，字段名 file），返回 { url }</summary>
    [HttpPost("{id:int}/image")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> UploadImage(int id, IFormFile? file)
    {
        try
        {
            var url = await _svc.SetImage(id, file);
            return Ok(new ApiResponse<object> { Data = new { url }, Message = "车辆图片已上传" });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>
    /// 直接以 URL 设置车辆图片（AI 生图结果落地用：生成成功后前端把 /uploads/ai/xxx.png
    /// 回传，服务端只做合法性与存在性校验，不再走 multipart 上传）。
    /// </summary>
    [HttpPut("{id:int}/image-url")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> SetImageUrl(int id, [FromBody] SetVehicleImageUrlDto dto)
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
    public async Task<IActionResult> DeleteImage(int id)
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
    public async Task<IActionResult> Create([FromBody] VehicleSaveDto dto)
    {
        try
        {
            var created = await _svc.Create(dto);
            return Ok(new ApiResponse<VehicleDto> { Data = VehicleDto.From(created) });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>
    /// 批量新增车辆：逐条创建、逐条校验，失败项不影响其它项。
    /// 返回逐条结果（成功/失败原因），前端据此展示「成功 N / 失败 M」。
    /// </summary>
    [HttpPost("batch")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> CreateBatch([FromBody] List<VehicleSaveDto> vehicles)
    {
        try
        {
            var data = await _svc.CreateBatch(vehicles);
            return Ok(new ApiResponse<BatchVehicleResultDto>
            {
                Data = data,
                Message = data.SuccessCount == data.Total
                    ? $"成功新增 {data.SuccessCount} 辆"
                    : $"成功 {data.SuccessCount} / 共 {data.Total} 辆"
            });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>
    /// 批量编辑车辆：仅覆盖被勾选的字段（Apply* 开关），未勾选保持原值。
    /// 状态变更需管理员密码二次校验（用况4 扩展用况3），密码从请求头 <c>X-Op-Password</c> 读取，
    /// 整批只校验一次；密码错误则整批中止。
    /// </summary>
    [HttpPut("batch")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> UpdateBatch([FromBody] BatchVehicleUpdateDto dto)
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
                Message = data.SuccessCount == data.Total
                    ? $"成功更新 {data.SuccessCount} 辆"
                    : $"成功 {data.SuccessCount} / 共 {data.Total} 辆"
            });
        }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>
    /// 更新车辆档案。档案编辑里若一并改动了「车辆状态」，同样要求二次密码校验
    /// （用况4 扩展用况3）——密码从请求头 <c>X-Op-Password</c> 读取，
    /// 未改状态时不校验，避免「改个品牌也要输密码」。
    /// </summary>
    [HttpPut("{id:int}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Update(int id, [FromBody] VehicleSaveDto dto)
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
    public async Task<IActionResult> Delete(int id)
    {
        try { await _svc.Delete(id); return Ok(new ApiResponse<object> { Message = "删除成功" }); }
        catch (Exception ex) { return BadRequest(new ApiResponse<object> { Code = 400, Message = ErrorText.Safe(ex), Success = false }); }
    }

    /// <summary>
    /// 手动更改车辆状态（用况4 步骤4 / 扩展用况3）。
    /// 「管理员操作车辆状态，使其变成其他状态需要管理员密码」——除角色授权外，
    /// 还要求随请求带上**当前登录管理员本人**的登录密码做二次校验，通过后才落库。
    /// 状态变更全程留痕（需求 4.2.2），日志由 VehicleService 写入。
    /// </summary>
    [HttpPut("{id:int}/status")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] ChangeVehicleStatusDto dto)
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

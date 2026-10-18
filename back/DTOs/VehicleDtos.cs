namespace ERental.DTOs;

/// <summary>
/// 车辆专属二维码的扫码结果（用况4 步骤3 / 需求 4.2.2「支持为车辆生成专属二维码，
/// 扫码可快速查看车辆档案与租赁记录」）。
///
/// 刻意做成 DTO 而不是直接返回 EF 实体：车辆与订单是双向导航，
/// 一旦把 <c>Vehicle.RentalOrders</c> 和 <c>RentalOrder.Vehicle</c> 一起序列化就会形成环。
/// </summary>
public class VehicleQrDetailDto
{
    public int VehicleId { get; init; }
    public string VehicleNo { get; init; } = string.Empty;
    public string? Brand { get; init; }
    public string? FrameNumber { get; init; }
    public string VehicleStatus { get; init; } = string.Empty;
    public DateTime? BuyTime { get; init; }
    public decimal? BuyPrice { get; init; }
    /// <summary>该车日租金；为空表示未单独定价，下单时回退系统配置</summary>
    public decimal? DailyRent { get; init; }
    /// <summary>该车押金；为空表示未单独定价，下单时回退系统配置</summary>
    public decimal? Deposit { get; init; }
    /// <summary>二维码内容，形如 ERENTAL:VEHICLE:V001</summary>
    public string? QrCode { get; init; }
    /// <summary>车辆当前是否处于在租状态</summary>
    public bool Rented { get; init; }
    /// <summary>进行中的订单（无则 null），前台扫码取车时直接看这一条</summary>
    public VehicleOrderBriefDto? CurrentOrder { get; init; }
    /// <summary>历史租赁记录（按起租时间倒序）</summary>
    public List<VehicleOrderBriefDto> Orders { get; init; } = [];
}

/// <summary>租赁记录摘要（车辆档案/扫码结果内嵌使用）</summary>
public class VehicleOrderBriefDto
{
    public int OrderId { get; init; }
    public string OrderNo { get; init; } = string.Empty;
    public string? CustomerName { get; init; }
    public string? ContactPhone { get; init; }
    public DateTime LeaseStart { get; init; }
    public DateTime LeaseEnd { get; init; }
    public DateTime? ActualReturn { get; init; }
    public string OrderStatus { get; init; } = string.Empty;
    public decimal TotalRent { get; init; }
    public decimal Deposit { get; init; }
}

/// <summary>热销车辆卡片（顾客线上租车页展示，含图片与小时价）</summary>
public class PopularVehicleDto
{
    public int VehicleId { get; init; }
    public string VehicleNo { get; init; } = string.Empty;
    public string? Brand { get; init; }
    /// <summary>车辆图片相对路径，无图时为 null（前端显示占位图标）</summary>
    public string? ImageUrl { get; init; }
    public string VehicleStatus { get; init; } = string.Empty;
    public decimal DailyRent { get; init; }
    public decimal HourlyRent { get; init; }
    public decimal Deposit { get; init; }
    /// <summary>统计区间内的成单量，用于「最近热销」排序与展示</summary>
    public int OrderCount { get; init; }
}

/// <summary>以 URL 直接设置车辆图片的入参（AI 生图结果落地）。</summary>
public class SetVehicleImageUrlDto
{
    /// <summary>本站内图片路径（/uploads/…）；传空 = 清除图片。</summary>
    public string? Url { get; init; }
}

/// <summary>
/// 手动更改车辆状态的入参（用况4 步骤4 / 扩展用况3：管理员操作车辆状态，
/// 使其变成其他状态需要管理员密码）。
/// 状态是「空闲/维保/报废」之一，「在租」由开单流程自动产生，不接受手工设置。
/// Password 为当前登录管理员的登录密码，服务端二次校验通过后才允许变更。
/// </summary>
public class ChangeVehicleStatusDto
{
    /// <summary>目标状态：空闲 / 维保 / 报废</summary>
    public string Status { get; init; } = string.Empty;
    /// <summary>当前登录管理员的登录密码（关键操作二次确认）</summary>
    public string Password { get; init; } = string.Empty;
}

/// <summary>批量操作（批量新增 / 批量编辑）的单条结果</summary>
public class BatchVehicleItemResult
{
    /// <summary>批次内的行序号（从 0 开始）或车辆主键，失败时方便定位是哪一行</summary>
    public int Index { get; init; }
    /// <summary>该行对应的车辆编号（入参里的 vehicleNo / 编辑时的库内编号）</summary>
    public string? VehicleNo { get; set; }
    /// <summary>是否成功</summary>
    public bool Success { get; set; }
    /// <summary>失败原因（成功时为 null）</summary>
    public string? Error { get; set; }
    /// <summary>成功后的车辆主键（失败时为 null）</summary>
    public int? VehicleId { get; set; }
}

/// <summary>
/// 批量操作总结果：逐条结果 + 成功计数。
/// 批量场景「部分成功」是常态（如某辆车编号重复），前端据此提示「成功 N / 失败 M」
/// 并把失败行单独列出，方便管理员定位修正后重试。
/// </summary>
public class BatchVehicleResultDto
{
    /// <summary>本次提交的总条数</summary>
    public int Total { get; init; }
    /// <summary>成功条数</summary>
    public int SuccessCount { get; set; }
    /// <summary>逐条结果（顺序与入参一致）</summary>
    public List<BatchVehicleItemResult> Items { get; init; } = [];
}

/// <summary>
/// 批量编辑入参：指定要编辑的车辆主键集合，以及「只覆盖勾选的字段」。
/// 每个字段配一个 Apply* 布尔开关——只有勾选了的字段才会写入，未勾选的保持原值，
/// 避免「批量把日租金清空」这类误伤。
/// 状态变更同样需要管理员密码（X-Op-Password 头，用况4 扩展用况3）。
/// </summary>
public class BatchVehicleUpdateDto
{
    /// <summary>要批量编辑的车辆主键列表</summary>
    public List<int> Ids { get; init; } = [];

    /// <summary>是否覆盖状态（需管理员密码二次校验）</summary>
    public bool ApplyStatus { get; init; }
    /// <summary>目标状态：空闲 / 维保 / 报废（「在租」由开单自动生成，不接受手工设置）</summary>
    public string? VehicleStatus { get; init; }

    /// <summary>是否覆盖日租金（留空 = 回退系统默认价，传 null）</summary>
    public bool ApplyDailyRent { get; init; }
    public decimal? DailyRent { get; init; }

    /// <summary>是否覆盖押金</summary>
    public bool ApplyDeposit { get; init; }
    public decimal? Deposit { get; init; }

    /// <summary>是否覆盖品牌</summary>
    public bool ApplyBrand { get; init; }
    public string? Brand { get; init; }
}

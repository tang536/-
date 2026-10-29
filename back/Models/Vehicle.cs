using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace ERental.Models;

[Table("vehicle")]
public class Vehicle
{
    [Key]
    [Column("vehicle_id")]
    public int VehicleId { get; init; }

    [Required, MaxLength(20)]
    [Column("vehicle_no")]
    public string VehicleNo { get; private set; } = string.Empty;

    [MaxLength(30)]
    [Column("frame_number")]
    public string? FrameNumber { get; private set; }

    [MaxLength(20)]
    [Column("brand")]
    public string? Brand { get; private set; }

    [Column("buy_time")]
    public DateTime? BuyTime { get; private set; }

    [Column("buy_price", TypeName = "decimal(10,2)")]
    public decimal? BuyPrice { get; private set; }

    /// <summary>
    /// 车辆状态。**带乐观并发令牌**。
    ///
    /// 为什么加 <see cref="ConcurrencyCheckAttribute"/>：
    /// 本表的占用/释放走裸 SQL（<c>UPDATE vehicle SET vehicle_status=… WHERE vehicle_status=…</c>，
    /// 见 VehicleLeaseGuard / VehicleStateGuard），而档案编辑走 EF 的 <c>SaveChanges</c>。
    /// 两条路径混用时有一个真实窗口：
    ///   顾客刚下单把车改成「在租」→ 管理员保存的编辑表单里状态仍是「空闲」（他是下单前打开的）→
    ///   EF 生成 <c>UPDATE vehicle SET vehicle_status='空闲' WHERE vehicle_id=?</c>，
    ///   **没有任何状态条件**，直接把裸 SQL 写的「在租」覆盖回「空闲」，
    ///   这辆车随后可以被第三个人再租一次。
    /// 加上这个特性后，EF 会把该列的**原始值**放进 UPDATE 的 WHERE：
    ///   <c>UPDATE … SET vehicle_status=@new WHERE vehicle_id=? AND vehicle_status=@original</c>
    ///   影响 0 行即抛 <c>DbUpdateConcurrencyException</c>，
    /// 由 <see cref="Services.ErrorText.Safe"/> 翻译成「这条数据刚才已被其他人修改，请刷新后重试」。
    ///
    /// 注意：这是**不加列**的乐观锁（不引入 version 字段），
    /// 因此不会破坏与设计文档「17 张表 / 134 字段」的一致性。
    /// </summary>
    [ConcurrencyCheck]
    [MaxLength(10)]
    [Column("vehicle_status")]
    public string VehicleStatus { get; private set; } = "空闲";

    [MaxLength(100)]
    [Column("qr_code")]
    public string? QrCode { get; private set; }

    /// <summary>
    /// 该车的日租金（元/天）。**每辆车可以有自己的租金**，不同品牌/车型的定价本就不同。
    /// 留空表示未单独定价，计价时回退 system_config 的 daily_rent —— 历史车辆无需补录。
    /// </summary>
    [Column("daily_rent", TypeName = "decimal(10,2)")]
    public decimal? DailyRent { get; private set; }

    /// <summary>该车的押金（元）。语义同 DailyRent：留空回退 system_config 的 deposit。</summary>
    [Column("deposit", TypeName = "decimal(10,2)")]
    public decimal? Deposit { get; private set; }

    /// <summary>
    /// 车辆图片地址（相对路径，如 /uploads/vehicle/xxx.jpg），用于线上租车页的车辆展示。
    /// 落盘规则与维保照片一致（见 ImageStorage），不建图片表。
    /// </summary>
    [MaxLength(255)]
    [Column("image_url")]
    public string? ImageUrl { get; private set; }

    // 导航集合：只给 EF 做关联查询用，**不进 JSON**。
    // 车辆档案接口（台账/详情）返回的是实体，若把这两个集合也序列化出去，
    // 一是把订单流水整片塞进车辆 JSON（响应体积与信息暴露都失控），
    // 二是 RentalOrder.Vehicle 又指回来，形成 Vehicle→RentalOrders→Vehicle 的环，
    // System.Text.Json 会直接抛 "A possible object cycle was detected"。
    [JsonIgnore] public ICollection<RentalOrder> RentalOrders { get; init; } = new List<RentalOrder>();
    [JsonIgnore] public ICollection<MaintenanceOrder> MaintenanceOrders { get; init; } = new List<MaintenanceOrder>();

    // ------------------------------------------------------------------ 工厂
    /// <summary>
    /// 新建车辆档案（管理员「新增车辆」，用况4 步骤2）。
    /// 主键由数据库自增，二维码要等主键落定后再由 <see cref="AssignQrCode"/> 写入，二者都不从入参来。
    /// </summary>
    public static Vehicle Create(string vehicleNo, string? frameNumber, string? brand, DateTime? buyTime, decimal? buyPrice, string vehicleStatus, decimal? dailyRent, decimal? deposit, string? imageUrl) => new()
        {
            VehicleNo = vehicleNo.Trim(),
            FrameNumber = Normalize(frameNumber),
            Brand = Normalize(brand),
            BuyTime = buyTime,
            BuyPrice = buyPrice,
            VehicleStatus = vehicleStatus,
            DailyRent = dailyRent,
            Deposit = deposit,
            ImageUrl = Normalize(imageUrl),
        };

    // -------------------------------------------------------------- 领域方法
    /// <summary>
    /// 编辑车辆档案（管理员「编辑车辆」）：档案字段整表单覆盖，
    /// 状态变更走 <see cref="ChangeStatus"/> 以便单独留痕与校验。
    /// 购入时间留空表示「不修改」（历史车辆往往没有采购单据）。
    /// </summary>
    public void ApplyProfile(string vehicleNo, string? frameNumber, string? brand, DateTime? buyTime, decimal? buyPrice, decimal? dailyRent, decimal? deposit)
    {
        VehicleNo = vehicleNo.Trim();
        FrameNumber = Normalize(frameNumber);
        Brand = Normalize(brand);
        if (buyTime is not null) BuyTime = buyTime;
        BuyPrice = buyPrice;
        DailyRent = dailyRent;   // 置空即回退系统配置的日租金
        Deposit = deposit;
    }

    /// <summary>
    /// 变更车辆状态（空闲 / 维保 / 报废）。「在租」由开单与归还流程独占写入，
    /// 手工设置由服务层拦下；状态列带乐观并发令牌，冲突时 EF 抛 DbUpdateConcurrencyException。
    /// </summary>
    public void ChangeStatus(string status) => VehicleStatus = status;

    /// <summary>批量编辑：只覆盖品牌（未勾选的字段不调用本方法，保持原值）。</summary>
    public void SetBrand(string? brand) => Brand = Normalize(brand);

    /// <summary>批量编辑：只覆盖日租金（null = 回退系统配置价）。</summary>
    public void SetDailyRent(decimal? dailyRent) => DailyRent = dailyRent;

    /// <summary>批量编辑：只覆盖押金（null = 回退系统配置价）。</summary>
    public void SetDeposit(decimal? deposit) => Deposit = deposit;

    /// <summary>设置或清除车辆图片；空串一律落成 NULL，避免库内出现两种「无图」。</summary>
    public void SetImage(string? url) => ImageUrl = Normalize(url);

    /// <summary>写入车辆专属二维码码值（由服务层按自增主键生成，一次性赋值）。</summary>
    public void AssignQrCode(string qrCode) => QrCode = qrCode;

    private static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

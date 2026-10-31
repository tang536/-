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

    /// <summary>车辆状态</summary>
    [ConcurrencyCheck]
    [MaxLength(10)]
    [Column("vehicle_status")]
    public string VehicleStatus { get; private set; } = "空闲";

    [MaxLength(100)]
    [Column("qr_code")]
    public string? QrCode { get; private set; }

    /// <summary>该车的日租金（元/天）</summary>
    [Column("daily_rent", TypeName = "decimal(10,2)")]
    public decimal? DailyRent { get; private set; }

    /// <summary>该车的押金（元）</summary>
    [Column("deposit", TypeName = "decimal(10,2)")]
    public decimal? Deposit { get; private set; }

    /// <summary>车辆图片地址（相对路径，如 /uploads/vehicle/xxx</summary>
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
    /// <summary>新建车辆档案（管理员「新增车辆」，用况4 步骤2）</summary>
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
    /// <summary>编辑车辆档案（管理员「编辑车辆」）：档案字段整表单覆盖， 状态变更走 以便单独留痕与校验</summary>
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

    /// <summary>变更车辆状态（空闲 / 维保 / 报废）</summary>
    public void ChangeStatus(string status) => VehicleStatus = status;

    /// <summary>批量编辑：只覆盖品牌（未勾选的字段不调用本方法，保持原值）</summary>
    public void SetBrand(string? brand) => Brand = Normalize(brand);

    /// <summary>批量编辑：只覆盖日租金（null = 回退系统配置价）</summary>
    public void SetDailyRent(decimal? dailyRent) => DailyRent = dailyRent;

    /// <summary>批量编辑：只覆盖押金（null = 回退系统配置价）</summary>
    public void SetDeposit(decimal? deposit) => Deposit = deposit;

    /// <summary>设置或清除车辆图片</summary>
    public void SetImage(string? url) => ImageUrl = Normalize(url);

    /// <summary>写入车辆专属二维码码值（由服务层按自增主键生成，一次性赋值）</summary>
    public void AssignQrCode(string qrCode) => QrCode = qrCode;

    private static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

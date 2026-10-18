using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

[Table("vehicle")]
public class Vehicle
{
    [Key]
    [Column("vehicle_id")]
    public int VehicleId { get; init; }

    [Required, MaxLength(20)]
    [Column("vehicle_no")]
    public string VehicleNo { get; set; } = string.Empty;

    [MaxLength(30)]
    [Column("frame_number")]
    public string? FrameNumber { get; set; }

    [MaxLength(20)]
    [Column("brand")]
    public string? Brand { get; set; }

    [Column("buy_time")]
    public DateTime? BuyTime { get; set; }

    [Column("buy_price", TypeName = "decimal(10,2)")]
    public decimal? BuyPrice { get; set; }

    [MaxLength(10)]
    [Column("vehicle_status")]
    public string VehicleStatus { get; set; } = "空闲";

    [MaxLength(100)]
    [Column("qr_code")]
    public string? QrCode { get; set; }

    /// <summary>
    /// 该车的日租金（元/天）。**每辆车可以有自己的租金**，不同品牌/车型的定价本就不同。
    /// 留空表示未单独定价，计价时回退 system_config 的 daily_rent —— 历史车辆无需补录。
    /// </summary>
    [Column("daily_rent", TypeName = "decimal(10,2)")]
    public decimal? DailyRent { get; set; }

    /// <summary>该车的押金（元）。语义同 DailyRent：留空回退 system_config 的 deposit。</summary>
    [Column("deposit", TypeName = "decimal(10,2)")]
    public decimal? Deposit { get; set; }

    /// <summary>
    /// 车辆图片地址（相对路径，如 /uploads/vehicle/xxx.jpg），用于线上租车页的车辆展示。
    /// 落盘规则与维保照片一致（见 ImageStorage），不建图片表。
    /// </summary>
    [MaxLength(255)]
    [Column("image_url")]
    public string? ImageUrl { get; set; }

    public ICollection<RentalOrder> RentalOrders { get; init; } = new List<RentalOrder>();
    public ICollection<MaintenanceOrder> MaintenanceOrders { get; init; } = new List<MaintenanceOrder>();
}

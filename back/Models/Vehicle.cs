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

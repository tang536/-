using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

[Table("maintenance_order")]
public class MaintenanceOrder
{
    [Key]
    [Column("work_order_id")]
    public int WorkOrderId { get; init; }

    [Required, MaxLength(30)]
    [Column("work_order_no")]
    public string WorkOrderNo { get; init; } = string.Empty;

    [Column("vehicle_id")]
    public int VehicleId { get; init; }

    [ForeignKey("VehicleId")]
    public Vehicle? Vehicle { get; init; }

    [MaxLength(50)]
    [Column("fault_type")]
    public string? FaultType { get; init; }

    [Column("maintain_fee", TypeName = "decimal(10,2)")]
    public decimal MaintainFee { get; set; }

    [MaxLength(10)]
    [Column("status")]
    public string Status { get; set; } = "待处理";

    [Column("create_time")]
    public DateTime CreateTime { get; init; } = DateTime.Now;

    [Column("finish_time")]
    public DateTime? FinishTime { get; set; }

    [MaxLength(200)]
    [Column("fault_description")]
    public string? FaultDescription { get; init; }

    /// <summary>
    /// 故障现场照片的相对访问路径（多张以英文逗号分隔），如 /uploads/maintenance/20260929_xxx.jpg。
    /// 用途：上报时拍照**锁定维保位置与故障现场状态**，作为后续定责与追溯的凭证。
    /// 工单创建后没有修改接口，因此照片一旦随工单落库即被锁定存档、不可篡改。
    /// </summary>
    [MaxLength(500)]
    [Column("fault_image")]
    public string? FaultImage { get; init; }

    [Column("operator_id")]
    public int? OperatorId { get; init; }
}

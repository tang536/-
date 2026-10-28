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
    public string? FaultType { get; private set; }

    [Column("maintain_fee", TypeName = "decimal(10,2)")]
    public decimal MaintainFee { get; private set; }

    [MaxLength(10)]
    [Column("status")]
    public string Status { get; private set; } = "待处理";

    [Column("create_time")]
    public DateTime CreateTime { get; init; } = DateTime.Now;

    [Column("finish_time")]
    public DateTime? FinishTime { get; private set; }

    [MaxLength(200)]
    [Column("fault_description")]
    public string? FaultDescription { get; private set; }

    /// <summary>
    /// 故障现场照片的相对访问路径（多张以英文逗号分隔），如 /uploads/maintenance/20260929_xxx.jpg。
    /// 用途：上报时拍照**锁定维保位置与故障现场状态**，作为后续定责与追溯的凭证。
    /// 工单创建后没有修改接口，因此照片一旦随工单落库即被锁定存档、不可篡改。
    /// </summary>
    [MaxLength(500)]
    [Column("fault_image")]
    public string? FaultImage { get; private set; }

    [Column("operator_id")]
    public int? OperatorId { get; init; }

    // ------------------------------------------------------------------ 工厂
    /// <summary>
    /// 上报故障并开工单（用况4 步骤8 / 用况4 扩展1）：工单号由服务层按当日流水生成，
    /// 维修费从 0 起（维修完成时才知道实际花费），状态从「待处理」起。
    /// 现场照片随工单一次性落库，工单没有修改接口，故照片不可被篡改。
    /// </summary>
    public static MaintenanceOrder Report(string workOrderNo, int vehicleId, string? faultType,
        string? faultDescription, string? faultImage, int? operatorId) => new()
        {
            WorkOrderNo = workOrderNo,
            VehicleId = vehicleId,
            FaultType = string.IsNullOrWhiteSpace(faultType) ? "其他" : faultType.Trim(),
            FaultDescription = faultDescription?.Trim(),
            FaultImage = string.IsNullOrWhiteSpace(faultImage) ? null : faultImage.Trim(),
            MaintainFee = 0,
            Status = "待处理",
            OperatorId = operatorId,
        };

    // -------------------------------------------------------------- 领域方法
    /// <summary>状态迁移（待处理 → 维修中 → 已完成 / 已撤回）。</summary>
    public void ChangeStatus(string status) => Status = status;

    /// <summary>完工：写维修费与完工时间并置「已完成」——三者必须同时落库，否则会留下「已完成但没费用」的脏工单。</summary>
    public void Complete(decimal maintainFee, DateTime finishTime)
    {
        MaintainFee = maintainFee;
        FinishTime = finishTime;
        Status = "已完成";
    }

    /// <summary>撤回工单：终态「已撤回」，车辆是否放回空闲由服务层按同车其它工单判定。</summary>
    public void Withdraw() => Status = "已撤回";
}

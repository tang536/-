using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ERental.Services;
namespace ERental.Models;

[Table("maintenance_order")]
public class MaintenanceOrder
{
    //三个状态值各只出现一次
    private const string StatusCompleted = "已完成";
    private const string StatusWithdrawn = "已撤回";

    [Key]
    [Column("work_order_id")]
    public int WorkOrderId { get; init; }

    [Required, MaxLength(30)]
    [Column("work_order_no")]
    public string WorkOrderNo { get; private set; } = string.Empty;

    [Column("vehicle_id")]
    public int VehicleId { get; private set; }

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

    ///<summary>故障现场照片的相对访问路径（多张以英文逗号分隔）</summary>
    [MaxLength(500)]
    [Column("fault_image")]
    public string? FaultImage { get; private set; }

    [Column("operator_id")]
    public int? OperatorId { get; private set; }

    ///<summary>上报故障并开工单：工单号由服务层按当日流水生成</summary>
    public static MaintenanceOrder Report(string workOrderNo, int vehicleId, string? faultType,
        string? faultDescription, string? faultImage, int? operatorId)
    {
        if (string.IsNullOrWhiteSpace(workOrderNo)) throw new BusinessException("维保工单号不能为空");
        if (vehicleId <= 0) throw new BusinessException("维保工单必须关联有效车辆");

        return new MaintenanceOrder
        {
            WorkOrderNo = workOrderNo.Trim(),
            VehicleId = vehicleId,
            FaultType = string.IsNullOrWhiteSpace(faultType) ? "其他" : faultType.Trim(),
            FaultDescription = faultDescription?.Trim(),
            FaultImage = string.IsNullOrWhiteSpace(faultImage) ? null : faultImage.Trim(),
            MaintainFee = 0,
            Status = "待处理",
            OperatorId = operatorId,
        };
    }

    ///<summary>完工：写维修费与完工时间并置「已完成」</summary>
    public void Complete(decimal maintainFee, DateTime finishTime)
    {
        if (Status == StatusWithdrawn) throw new BusinessException("该工单已撤回，不能再完工");
        if (Status == StatusCompleted) throw new BusinessException("该工单已完成，无需重复操作");
        if (maintainFee < 0) throw new BusinessException("维修费不能为负数");

        MaintainFee = maintainFee;
        FinishTime = finishTime;
        Status = StatusCompleted;
    }

    ///<summary>撤回工单：终态「已撤回」，车辆是否放回空闲由服务层按同车其它工单判定</summary>
    public void Withdraw()
    {
        if (Status == StatusCompleted) throw new BusinessException("已完成的工单不可撤回");
        if (Status == StatusWithdrawn) throw new BusinessException("该工单已撤回");

        Status = StatusWithdrawn;
    }
}

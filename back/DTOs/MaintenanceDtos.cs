using System.ComponentModel.DataAnnotations;

namespace ERental.DTOs;

/// <summary>维保工单出参（投影，避免实体导航属性形成循环引用）</summary>
public class MaintenanceOrderDto
{
    public int WorkOrderId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public int VehicleId { get; init; }
    public string VehicleNo { get; init; } = string.Empty;
    public string? Brand { get; init; }
    public string? FaultType { get; init; }
    public string? FaultDescription { get; init; }
    public decimal MaintainFee { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime CreateTime { get; init; }
    public DateTime? FinishTime { get; init; }
    public int? OperatorId { get; init; }
    /// <summary>故障现场照片路径（多张以逗号分隔），用于查看已锁定的维保位置</summary>
    public string? FaultImage { get; init; }
}

/// <summary>报废记录出参</summary>
public class ScrapRecordDto
{
    public int ScrapId { get; init; }
    public int VehicleId { get; init; }
    public string VehicleNo { get; init; } = string.Empty;
    public string? Brand { get; init; }
    public string? ScrapReason { get; init; }
    public DateTime ScrapTime { get; init; }
    public int? OperatorId { get; init; }
}

/// <summary>创建维保工单入参（工单号由后端生成，前端不传）</summary>
public class CreateMaintenanceRequest
{
    [Range(1, int.MaxValue, ErrorMessage = "请选择故障车辆")]
    public int VehicleId { get; init; }

    [MaxLength(50, ErrorMessage = "故障类型过长")]
    public string? FaultType { get; init; }

    [Required(ErrorMessage = "请填写故障描述")]
    [MaxLength(200, ErrorMessage = "故障描述不得超过 200 字")]
    public string FaultDescription { get; init; } = string.Empty;

    /// <summary>
    /// 故障现场照片路径（先调 /maintenance/upload 拿到路径，再随工单提交）。
    /// 多张以英文逗号分隔；为空表示未拍照。
    /// </summary>
    [MaxLength(500, ErrorMessage = "照片路径过长")]
    public string? FaultImage { get; init; }
}

/// <summary>完成维保入参</summary>
public class CompleteMaintenanceRequest
{
    [Range(0, 999999.99, ErrorMessage = "维修费用不能为负数")]
    public decimal Fee { get; init; }
}

/// <summary>编辑维保工单入参（前台 / 管理员均可修改内容）。已完成工单不可再编辑。</summary>
public class UpdateMaintenanceRequest
{
    [MaxLength(50, ErrorMessage = "故障类型过长")]
    public string? FaultType { get; init; }

    [Required(ErrorMessage = "请填写故障描述")]
    [MaxLength(200, ErrorMessage = "故障描述不得超过 200 字")]
    public string FaultDescription { get; init; } = string.Empty;

    /// <summary>故障现场照片路径（逗号分隔）。留空表示清空已锁定的照片。</summary>
    [MaxLength(500, ErrorMessage = "照片路径过长")]
    public string? FaultImage { get; init; }
}

/// <summary>车辆报废入参</summary>
public class ScrapRequest
{
    public int VehicleId { get; init; }
    public string Reason { get; init; } = "";
}

namespace ERental.DTOs;

/// <summary>车辆盘点录入入参：系统状态由车辆档案带出，只需报实际状态与差异说明</summary>
public class CheckRequest
{
    public int VehicleId { get; init; }
    public string ActualStatus { get; init; } = string.Empty;
    public string? DiffDesc { get; init; }
}

/// <summary>生成盘点报告入参：Date 缺省为当天</summary>
public class GenReportRequest
{
    public DateTime Date { get; init; } = DateTime.Now;
}

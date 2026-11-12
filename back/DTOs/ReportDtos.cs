namespace ERental.DTOs;

/// <summary>
/// 财务下钻明细的一行。
/// 三个维度（时间 / 订单 / 商户）共用同一个行结构，而不是各写一套 DTO：
/// 行结构一致 → 前端只写一个表格渲染器，新增维度时后端加分支即可，
/// 不会出现"三个维度的表格各写一遍、改一处漏两处"的情况。
/// </summary>
public class DrillRowDto
{
    /// <summary>维度键：日期（MM-dd）/ 订单号 / 收单渠道名</summary>
    public string Key { get; init; } = string.Empty;
    /// <summary>展示名（多数维度与 Key 相同；订单维度为订单号）</summary>
    public string Label { get; init; } = string.Empty;
    public int OrderCount { get; init; }
    public int PayCount { get; init; }
    /// <summary>应收租金</summary>
    public decimal Rent { get; init; }
    /// <summary>应收押金</summary>
    public decimal Deposit { get; init; }
    /// <summary>实收（已支付流水合计）</summary>
    public decimal Received { get; init; }
    public decimal Refund { get; init; }
    /// <summary>净额 = 实收 - 退款</summary>
    public decimal Net { get; init; }
    /// <summary>占比（%），商户维度用；其他维度为 0</summary>
    public decimal Share { get; init; }
    /// <summary>附加说明（订单维度带客户 / 车辆 / 支付方式 / 状态）</summary>
    public string Ext { get; init; } = string.Empty;
}

/// <summary>财务下钻结果：汇总（金额构成）+ 明细行</summary>
public class FinanceDrillDto
{
    /// <summary>当前维度：time / order / merchant</summary>
    public string Dimension { get; init; } = "time";
    public string DimensionName { get; init; } = string.Empty;
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;

    // ===== 金额构成（汇总）=====
    public decimal TotalRent { get; init; }
    public decimal TotalDeposit { get; init; }
    public decimal TotalReceived { get; init; }
    public decimal TotalRefund { get; init; }
    public decimal NetIncome { get; set; }
    public int OrderCount { get; init; }
    public int PayCount { get; init; }

    /// <summary>表头（与 <see cref="DrillRowDto"/> 字段一一对应，前端按此渲染）</summary>
    public List<string> Columns { get; set; } = [];
    public List<DrillRowDto> Rows { get; set; } = [];
}

/// <summary>
/// 车辆运营统计的一行（需求 4.2.7「车辆运营统计」）。
/// 每台车一行，把「这台车给我赚了多少、占用了多久」摆在同一行里，
/// 便于识别高价值车辆（营收高、时长长）与闲置车辆（时长为 0）、
/// 以及「越修越亏」的车（维保费用接近甚至超过营收）。
/// </summary>
public class VehicleStatRowDto
{
    public int VehicleId { get; init; }
    public string VehicleNo { get; init; } = string.Empty;
    public string? Brand { get; init; }
    /// <summary>当前状态：空闲 / 在租 / 维保 / 报废</summary>
    public string VehicleStatus { get; init; } = string.Empty;
    /// <summary>区间内订单数</summary>
    public int OrderCount { get; init; }
    /// <summary>累计租赁时长（小时，按订单租期 LeaseEnd - LeaseStart 累加）</summary>
    public decimal RentHours { get; init; }
    /// <summary>累计营收（车辆所关联订单的已支付流水合计）</summary>
    public decimal Revenue { get; init; }
    /// <summary>维保次数（区间内工单数）</summary>
    public int MaintenanceCount { get; init; }
    /// <summary>维保费用（区间内已完工工单费用合计）</summary>
    public decimal MaintenanceFee { get; init; }
    /// <summary>最近一次租赁开始时间（yyyy-MM-dd HH:mm；无租赁记录时为空）</summary>
    public string? LastRentTime { get; init; }
}

/// <summary>车辆运营统计结果（每台车明细 + 车队合计）</summary>
public class VehicleStatDto
{
    /// <summary>统计起始日（yyyy-MM-dd；未传区间时为空串，表示累计口径）</summary>
    public string From { get; init; } = string.Empty;
    /// <summary>统计截止日（yyyy-MM-dd）</summary>
    public string To { get; init; } = string.Empty;
    public int TotalVehicles { get; init; }
    /// <summary>区间内没有任何租赁记录的车辆数（即这段区间里闲置没出过车）</summary>
    public int IdleVehicles { get; init; }
    public decimal TotalRentHours { get; init; }
    public decimal TotalRevenue { get; init; }
    public int TotalMaintenanceCount { get; init; }
    public decimal TotalMaintenanceFee { get; init; }
    public List<VehicleStatRowDto> Rows { get; init; } = [];
}

/// <summary>故障类型费用分布的一行（需求 4.2.7「故障类型费用分布」）</summary>
public class FaultTypeStatDto
{
    public string FaultType { get; init; } = string.Empty;
    /// <summary>该故障类型的工单数</summary>
    public int Count { get; init; }
    /// <summary>该故障类型的维保费用合计（仅已完工工单）</summary>
    public decimal Fee { get; init; }
    /// <summary>费用占比（%，按已完工费用计）</summary>
    public decimal Share { get; set; }
}

/// <summary>单台车辆维保费用排行的一行（需求 4.2.7「单台车辆维保费用排行」）</summary>
public class MaintenanceRankDto
{
    public int VehicleId { get; init; }
    public string VehicleNo { get; init; } = string.Empty;
    public string? Brand { get; init; }
    /// <summary>工单数</summary>
    public int Count { get; init; }
    /// <summary>维保费用合计（仅已完工工单）</summary>
    public decimal Fee { get; init; }
}

/// <summary>
/// 维保费用统计结果（需求 4.2.7「维保费用统计」）。
/// 三个视角合在一个响应里：时间（按月趋势）、故障类型（钱花在什么毛病上）、
/// 车辆（钱花在哪台车上），对应报表页的三块内容。
/// </summary>
public class MaintenanceStatDto
{
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
    /// <summary>区间内工单总数（含未完工）</summary>
    public int TotalCount { get; init; }
    /// <summary>区间内维保总费用（仅已完工工单）</summary>
    public decimal TotalFee { get; init; }
    /// <summary>按月聚合的维保总费用（yyyy-MM → 金额，仅已完工工单）</summary>
    public Dictionary<string, decimal> MonthlyFee { get; init; } = new();
    /// <summary>故障类型费用分布（按费用降序）</summary>
    public List<FaultTypeStatDto> FaultTypes { get; init; } = [];
    /// <summary>单台车辆维保费用排行（按费用降序，取前 10 台）</summary>
    public List<MaintenanceRankDto> TopVehicles { get; init; } = [];
}

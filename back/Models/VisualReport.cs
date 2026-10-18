namespace ERental.Models;

/// <summary>
/// 可视化报表：统计看板的计算结果，非数据库映射。
/// 既是 <c>GET /api/report/dashboard</c> 的 JSON 载荷（前端 Dashboard / Reports 直接读字段），
/// 也是 <c>/api/print/export/revenue</c> 导出 CSV 的数据源。
/// </summary>
public record VisualReport
{
    // ===== 累计指标（统计报表页使用）=====
    public decimal TotalRevenue { get; init; }
    public int TotalOrders { get; init; }
    /// <summary>客户总数。首页看板与统计报表共用同一字段，不再另设同义属性</summary>
    public int TotalCustomers { get; init; }
    /// <summary>总的维修费用</summary>
    public decimal TotalMaintenanceFee { get; init; }
    public Dictionary<string, decimal>? MonthlyRevenue { get; init; }
    public Dictionary<string, int>? VehicleStatusStats { get; init; }
    public Dictionary<string, int>? OrderStatusStats { get; init; }

    // ===== 首页核心数据看板指标（对应需求 6.1「核心数据看板」）=====
    /// <summary>今日营收</summary>
    public decimal TodayRevenue { get; init; }
    /// <summary>今日新增订单</summary>
    public int TodayOrders { get; init; }
    /// <summary>在租车辆数</summary>
    public int RentedCount { get; init; }
    /// <summary>空闲可租车辆数</summary>
    public int FreeCount { get; init; }
    /// <summary>进行中订单数（已支付/租赁中）</summary>
    public int ActiveOrderCount { get; init; }
    /// <summary>逾期未还订单数</summary>
    public int OverdueCount { get; init; }
    /// <summary>24 小时内到期订单数</summary>
    public int ExpiringCount { get; init; }
    /// <summary>待支付订单数</summary>
    public int PendingPayCount { get; init; }
    /// <summary>维保中车辆数</summary>
    public int MaintenanceCount { get; init; }
    /// <summary>资料待完善的客户数（缺身份证号或手机号）</summary>
    public int IncompleteProfileCount { get; init; }

    // ===== 运营指标（管理员端统计报表专用，与财务口径区分）=====
    // 设计口径：管理员看「车辆转得动不动、订单来不来得及」，
    // 财务看「钱收了多少、账平不平」。两者不重叠，避免同一张报表被两种角色当成同一件事。

    /// <summary>车辆使用率（%）：区间内被租出过的车辆数 / 全部车辆数</summary>
    public decimal VehicleUsageRate { get; init; }

    /// <summary>区间内被租出过的车辆数（去重）</summary>
    public int RentedVehicleCount { get; init; }

    /// <summary>车辆总数（使用率分母）</summary>
    public int TotalVehicleCount { get; init; }

    /// <summary>平均单笔租期（小时）：区间内订单的平均时长。0 表示无有效订单</summary>
    public decimal AvgRentHours { get; init; }

    /// <summary>单台车平均产出（元/台）：区间营收 / 车辆总数；衡量车队赚钱效率</summary>
    public decimal RevenuePerVehicle { get; init; }

    /// <summary>订单取消率（%）：区间内已取消订单 / 区间内全部订单</summary>
    public decimal CancelRate { get; init; }

    /// <summary>订单趋势（日期 → 订单数）。与营收趋势同区间，用于观察"单量"而非"金额"</summary>
    public Dictionary<string, int>? OrderTrend { get; init; }
}

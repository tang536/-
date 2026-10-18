namespace ERental.Services;

/// <summary>
/// 车辆状态的唯一合法取值集合（需求 4.2.2 与《可行性研究报告》1.3 定义）：
/// 空闲 / 在租 / 维保 / 报废。
///
/// 为什么要有这个白名单：车辆状态是订单派单、报废、盘点对账的判断依据，
/// 而状态既可能由管理员手动调整，也可能由盘点（实际状态）录入。
/// 若不做校验，库里会出现「飞行中」这类取值，直接破坏
/// 需求 5.1「状态数据精度：车辆状态实时同步、无状态不一致」的要求，
/// 也会让按状态筛选、车辆可用性判断全部失真。
/// </summary>
public static class VehicleStatuses
{
    public const string Idle = "空闲";
    public const string Rented = "在租";
    public const string Maintenance = "维保";
    public const string Scrapped = "报废";

    private static readonly string[] All = [Idle, Rented, Maintenance, Scrapped];

    public static bool IsValid(string? status) =>
        !string.IsNullOrWhiteSpace(status) && All.Contains(status);

    /// <summary>统一的错误文案（含全部合法取值，便于用户直接改正）。</summary>
    public static string InvalidMessage(string field = "车辆状态") =>
        $"{field}不合法，只能是：{string.Join(" / ", All)}";
}

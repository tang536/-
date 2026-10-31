namespace ERental.Services;

/// <summary>车辆状态的唯一合法取值集合（需求 4</summary>
public static class VehicleStatuses
{
    public const string Idle = "空闲";
    public const string Rented = "在租";
    public const string Maintenance = "维保";
    public const string Scrapped = "报废";

    private static readonly string[] All = [Idle, Rented, Maintenance, Scrapped];

    public static bool IsValid(string? status) =>
        !string.IsNullOrWhiteSpace(status) && All.Contains(status);

    /// <summary>统一的错误文案（含全部合法取值，便于用户直接改正）</summary>
    public static string InvalidMessage(string field = "车辆状态") =>
        $"{field}不合法，只能是：{string.Join(" / ", All)}";
}

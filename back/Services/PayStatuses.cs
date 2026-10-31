namespace ERental.Services;

/// <summary>支付状态口径：「钱到过账」的三种状态</summary>
public static class PayStatuses
{
    /// <summary>钱到过账的状态集合</summary>
    public static string[] MoneyArrived { get; } = ["已支付", "部分退款", "已退款"];

    /// <summary>下单时写在支付记录里的渠道占位值（顾客还没点「去支付」）</summary>
    public const string ChannelPlaceholder = "待定";

    /// <summary>内存集合判断用（如对已取回的 List 过滤），与 同口径</summary>
    public static bool IsMoneyArrived(string? status) =>
        status is "已支付" or "部分退款" or "已退款";
}

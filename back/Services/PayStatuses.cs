namespace ERental.Services;

/// <summary>
/// 支付状态口径：「钱到过账」的三种状态。
///
/// 结算退押金会把支付记录改写为「部分退款」（退满为「已退款」）——钱明明收过，
/// 只数「已支付」会把已完结订单整批漏掉：对账差额 28,646.72 的根因就在这里
/// （14 笔部分退款记录合计）。营收 / 实收 / 已付金额类统计必须同一口径，
/// 否则同一系统会出现两套数（对账页说实收 118,736.52、营收报表说 90,089.80）。
///
/// 「待支付」与「已取消」（下单后取消）不在此列，天然排除。
/// </summary>
public static class PayStatuses
{
    /// <summary>
    /// 钱到过账的状态集合。EF Core 会把数组 <c>Contains</c> 翻译成 SQL <c>IN</c>，
    /// 可直接用于 <see cref="IQueryable{T}"/>；LINQ to Objects 同样适用。
    ///
    /// 用只读自动属性暴露：后备字段由编译器生成且必为 <c>private</c>，
    /// 集合构造后不再改变，类外无法替换整个数组，避免被误赋值为别的口径。
    /// </summary>
    public static string[] MoneyArrived { get; } = ["已支付", "部分退款", "已退款"];

    /// <summary>
    /// 下单时写在支付记录里的渠道占位值（顾客还没点「去支付」）。
    ///
    /// 用途：查单兜底要据此判断「这笔到底有没有真的发到渠道」。
    /// 占位状态意味着渠道侧根本没有这笔交易，此时向支付宝/微信查单
    /// 不仅必然返回「交易不存在」，还白搭一次远程调用与外网超时。
    /// </summary>
    public const string ChannelPlaceholder = "待定";

    /// <summary>内存集合判断用（如对已取回的 List 过滤），与 <see cref="MoneyArrived"/> 同口径。</summary>
    public static bool IsMoneyArrived(string? status) =>
        status is "已支付" or "部分退款" or "已退款";
}

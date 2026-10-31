namespace ERental.Services;

/// <summary>结算金额的纯计算部分（不碰数据库，便于单元测试锁住口径）</summary>
public static class SettlementMath
{
    /// <summary>结算净额</summary>
    public static (decimal RentTotal, decimal RentDiff, decimal Refund, decimal Supplement) Settle(
        decimal prepaidRent, decimal deposit, decimal damageFee,
        decimal actualRent, decimal overdueFee)
    {
        var rentTotal = Math.Round(actualRent + overdueFee, 2);
        var rentDiff = Math.Round(prepaidRent - rentTotal, 2);
        var net = Math.Round(rentDiff + deposit - damageFee, 2);

        // 净额为负表示顾客还要补钱，此时退款额必须是 0（而不是负数），避免下游把负数当退款发起
        return (rentTotal, rentDiff, Math.Max(0m, net), net < 0 ? -net : 0m);
    }
}

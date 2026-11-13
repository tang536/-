using ERental.Services;

namespace ERental.Tests;

/// <summary>计价与结算的纯计算：与 SQL 函数 fn_effective_daily_rent / fn_billable_hours 同口径</summary>
public class PricingAndSettlementTests
{
    [Theory]
    [InlineData("35", "1.46")]
    [InlineData("10", "0.42")]
    [InlineData("30", "1.25")]
    [InlineData("0", "0.00")]
    public void HourlyRent_RoundsToCent(string daily, string expected)
    {
        Assert.Equal(decimal.Parse(expected), PricingResolver.HourlyRent(decimal.Parse(daily)));
    }

    [Fact]
    public void CalcHours_RoundsUpAndNeverBelowOne()
    {
        var start = new DateTime(2026, 11, 13, 8, 0, 0);
        Assert.Equal(1, PricingResolver.CalcHours(start, start));
        Assert.Equal(1, PricingResolver.CalcHours(start, start.AddMinutes(1)));
        Assert.Equal(2, PricingResolver.CalcHours(start, start.AddMinutes(61)));
        Assert.Equal(24, PricingResolver.CalcHours(start, start.AddHours(24)));
        Assert.Equal(1, PricingResolver.CalcHours(start, start.AddMinutes(-30)));
    }

    [Fact]
    public void Settle_NoOverdue_RefundsWholeDeposit()
    {
        var (rentTotal, rentDiff, refund, supplement) =
            SettlementMath.Settle(60m, 500m, 0m, 60m, 0m);

        Assert.Equal(60m, rentTotal);
        Assert.Equal(0m, rentDiff);
        Assert.Equal(500m, refund);
        Assert.Equal(0m, supplement);
    }

    [Fact]
    public void Settle_OverdueAndDamage_ProducesSupplementAndNoNegativeRefund()
    {
        var (rentTotal, rentDiff, refund, supplement) =
            SettlementMath.Settle(60m, 200m, 300m, 90m, 45m);

        Assert.Equal(135m, rentTotal);
        Assert.Equal(-75m, rentDiff);
        Assert.Equal(0m, refund);
        Assert.Equal(175m, supplement);
    }

    [Fact]
    public void Settle_AmountsAreRoundedToCent()
    {
        var (rentTotal, _, refund, _) = SettlementMath.Settle(33.333m, 100m, 0m, 33.333m, 0.005m);

        Assert.Equal(33.34m, rentTotal);
        Assert.Equal(99.99m, refund);
    }
}

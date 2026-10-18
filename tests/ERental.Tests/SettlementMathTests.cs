using ERental.Services;

namespace ERental.Tests;

/// <summary>
/// 结算「多退少补」金额测试。这组用例覆盖四类真实场景：
/// 提前还车（应多退）、按时还车（只退押金）、逾期（扣押金补租金）、损坏（扣损坏费）。
/// </summary>
public class SettlementMathTests
{
    [Fact]
    public void OnTime_ReturnsDepositInFull()
    {
        // 预付 30、实租 30，押金 500，无损坏 → 只退押金 500
        var r = SettlementMath.Settle(
            prepaidRent: 30m, deposit: 500m, damageFee: 0m,
            actualRent: 30m, overdueFee: 0m);

        Assert.Equal(30m, r.RentTotal);
        Assert.Equal(0m, r.RentDiff);
        Assert.Equal(500m, r.Refund);
        Assert.Equal(0m, r.Supplement);
    }

    [Fact]
    public void EarlyReturn_RefundsUnusedRentPlusDeposit()
    {
        // 预付 3 天 90 元，实租 1 天 30 元 → 退 60 租金 + 500 押金 = 560
        var r = SettlementMath.Settle(
            prepaidRent: 90m, deposit: 500m, damageFee: 0m,
            actualRent: 30m, overdueFee: 0m);

        Assert.Equal(60m, r.RentDiff);
        Assert.Equal(560m, r.Refund);
        Assert.Equal(0m, r.Supplement);
    }

    [Fact]
    public void Overdue_WithEnoughDeposit_DeductsFromDeposit()
    {
        // 预付 30、实租 30 + 逾期 15 = 45；租金差 -15，押金 500 → 净退 485
        var r = SettlementMath.Settle(
            prepaidRent: 30m, deposit: 500m, damageFee: 0m,
            actualRent: 30m, overdueFee: 15m);

        Assert.Equal(45m, r.RentTotal);          // 逾期费计入租金合计
        Assert.Equal(-15m, r.RentDiff);
        Assert.Equal(485m, r.Refund);
        Assert.Equal(0m, r.Supplement);
    }

    [Fact]
    public void Damage_DeductsFromDeposit()
    {
        // 押金 500，损坏费 200 → 退 300
        var r = SettlementMath.Settle(
            prepaidRent: 30m, deposit: 500m, damageFee: 200m,
            actualRent: 30m, overdueFee: 0m);

        Assert.Equal(300m, r.Refund);
        Assert.Equal(0m, r.Supplement);
    }

    [Fact]
    public void LossExceedsDeposit_ProducesSupplementNotNegativeRefund()
    {
        // 押金 100，损坏 200、租金差 -15 → 顾客应补 115，退款额必须是 0（不能是 -115）
        var r = SettlementMath.Settle(
            prepaidRent: 30m, deposit: 100m, damageFee: 200m,
            actualRent: 30m, overdueFee: 15m);

        Assert.Equal(0m, r.Refund);
        Assert.Equal(115m, r.Supplement);
    }

    [Fact]
    public void RefundAndSupplement_AreMutuallyExclusive()
    {
        // 幂等性质：任何输入下都不会同时 > 0，避免既退款又补缴
        var cases = new (decimal Pre, decimal Dep, decimal Dmg, decimal Rent, decimal Over)[]
        {
            (0m, 0m, 0m, 0m, 0m),
            (30m, 500m, 0m, 30m, 0m),
            (30m, 0m, 999m, 30m, 0m),
            (90m, 500m, 0m, 10m, 0m),
        };

        foreach (var c in cases)
        {
            var r = SettlementMath.Settle(c.Pre, c.Dep, c.Dmg, c.Rent, c.Over);
            Assert.False(r.Refund > 0 && r.Supplement > 0,
                $"退款 {r.Refund} 与补缴 {r.Supplement} 不应同时发生");
        }
    }

    [Fact]
    public void ResultsAreRoundedToCent()
    {
        // 0.005 级尾数必须收敛到 2 位小数，否则页面显示与库内 decimal(10,2) 会差一分
        var r = SettlementMath.Settle(
            prepaidRent: 20.005m, deposit: 500.005m, damageFee: 0m,
            actualRent: 16.25m, overdueFee: 0m);

        Assert.Equal(Math.Round(r.RentTotal, 2), r.RentTotal);
        Assert.Equal(Math.Round(r.Refund, 2), r.Refund);
        Assert.Equal(Math.Round(r.Supplement, 2), r.Supplement);
    }
}

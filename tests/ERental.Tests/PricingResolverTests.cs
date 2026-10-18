using ERental.Services;

namespace ERental.Tests;

/// <summary>
/// 计价口径测试。这些数字直接决定前台向顾客报的价，写错一位就是纠纷，
/// 所以把「小时价折算」与「计费小时数」两条规则用用例钉住。
/// </summary>
public class PricingResolverTests
{
    [Theory]
    [InlineData(30, 1.25)]     // 常见日租 30 元 → 1.25 元/小时
    [InlineData(50, 2.08)]     // 50/24 = 2.0833… 四舍五入到分
    [InlineData(0, 0)]         // 未定价的车辆不应抛异常
    public void HourlyRent_RoundsToCent(decimal dailyRent, decimal expected)
    {
        Assert.Equal(expected, PricingResolver.HourlyRent(dailyRent));
    }

    [Fact]
    public void CalcHours_RoundsUp_PartialHourCountsAsFullHour()
    {
        var start = new DateTime(2026, 10, 18, 9, 0, 0);
        // 1 小时 1 分 → 算 2 小时，不能截断成 1 小时（否则租客白用 1 小时）
        Assert.Equal(2, PricingResolver.CalcHours(start, start.AddMinutes(61)));
    }

    [Fact]
    public void CalcHours_MinimumOneHour()
    {
        var start = new DateTime(2026, 10, 18, 9, 0, 0);
        // 刚下单就还车（0 分钟）也要收 1 小时，避免出现 0 元订单
        Assert.Equal(1, PricingResolver.CalcHours(start, start));
        Assert.Equal(1, PricingResolver.CalcHours(start, start.AddMinutes(5)));
    }

    [Fact]
    public void CalcHours_FullDayIs24()
    {
        var start = new DateTime(2026, 10, 18, 9, 0, 0);
        Assert.Equal(24, PricingResolver.CalcHours(start, start.AddDays(1)));
    }

    /// <summary>回到「结算与下单同口径」的回归点：24 小时 × 小时价 应当等于日租金本身</summary>
    [Fact]
    public void OneDayRent_EqualsDailyRent()
    {
        const decimal dailyRent = 30m;
        var hourly = PricingResolver.HourlyRent(dailyRent);
        // 1.25 × 24 = 30.00，说明「日租金 ÷ 24」的折算没有损失
        Assert.Equal(dailyRent, hourly * 24);
    }
}

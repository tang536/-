using ERental.Services;

namespace ERental.Tests;

/// <summary>
/// 退款幂等键测试。幂等键是「网关成功但本地回滚后重试」不重复退款的唯一保障，
/// 因此重点验证两点：同输入必须得到同键；退到同一累计额只对应一个键。
/// </summary>
public class RefundKeyTests
{
    [Fact]
    public void Format_IsPayIdThenCumulativeCents()
    {
        Assert.Equal("128R15000", RefundKey.For(128, 150m));
    }

    [Fact]
    public void SameCumulativeAmount_ProducesSameKey_EvenIfPaidInSteps()
    {
        // 一笔 100 元分两次各退 25 元：第二次退完时累计 50 元
        var afterFirst = RefundKey.For(7, 25m);
        var afterSecond = RefundKey.For(7, 50m);

        Assert.Equal("7R2500", afterFirst);
        Assert.Equal("7R5000", afterSecond);
        Assert.NotEqual(afterFirst, afterSecond);
    }

    [Fact]
    public void Retry_WithSameCumulativeAmount_IsIdempotent()
    {
        // 网关成功、本地事务回滚后重试：累计额不变 → 键不变 → 网关识别为同一笔
        Assert.Equal(RefundKey.For(7, 50m), RefundKey.For(7, 50m));
    }

    [Theory]
    [InlineData(0.1, 10)]
    [InlineData(0.29, 29)]
    [InlineData(19.99, 1999)]
    [InlineData(1234.56, 123456)]
    public void Cents_AreExact_WithoutFloatingResidue(decimal amount, int expectedCents)
    {
        Assert.Equal($"{1}R{expectedCents}", RefundKey.For(1, amount));
    }

    [Fact]
    public void HalfCent_RoundsAwayFromZero()
    {
        // 0.005 元（半分）在财务口径上应进位到 1 分，而不是被银行家舍入吞掉
        Assert.Equal("1R1", RefundKey.For(1, 0.005m));
    }

    [Fact]
    public void DifferentPayRecords_NeverCollide()
    {
        // 不同支付记录即使用同样的累计退款额，键也必须不同（否则网关会把两笔当成一笔）
        Assert.NotEqual(RefundKey.For(11, 50m), RefundKey.For(12, 50m));
    }
}

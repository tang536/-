using ERental.Data;

namespace ERental.Tests;

/// <summary>会话时区格式化：MySQL 只接受 ±HH:MM 形式的数字偏移</summary>
public class SessionTimeZoneTests
{
    [Theory]
    [InlineData(0, "+00:00")]
    [InlineData(480, "+08:00")]
    [InlineData(-300, "-05:00")]
    [InlineData(-330, "-05:30")]
    [InlineData(345, "+05:45")]
    [InlineData(840, "+14:00")]
    public void FormatOffset_ReturnsMySqlNumericOffset(int minutes, string expected)
    {
        Assert.Equal(expected, MySqlSessionTimeZoneInterceptor.FormatOffset(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void CurrentOffset_MatchesLocalTimeZone()
    {
        var expected = MySqlSessionTimeZoneInterceptor.FormatOffset(TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.Now));

        Assert.Equal(expected, MySqlSessionTimeZoneInterceptor.CurrentOffset());
        Assert.Matches(@"^[+-]\d{2}:\d{2}$", MySqlSessionTimeZoneInterceptor.CurrentOffset());
    }
}

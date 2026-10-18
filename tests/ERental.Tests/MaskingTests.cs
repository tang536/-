using ERental.Services;

namespace ERental.Tests;

/// <summary>
/// 脱敏测试。小票与日志是最容易流出身份证、密钥的两处（小票会被顾客带走、随手丢），
/// 需求 6.4「敏感信息脱敏」要求这两处必须过统一工具。
/// </summary>
public class MaskingTests
{
    [Fact]
    public void IdCard_KeepsFirstAndLastFour()
    {
        var masked = PrintService.MaskIdCard("440305199001011234");
        Assert.Equal("4403**********1234", masked);
        Assert.DoesNotContain("19900101", masked);   // 生日不得出现在小票上
    }

    [Fact]
    public void IdCard_EmptyOrNull_ShowsDash()
    {
        Assert.Equal("—", PrintService.MaskIdCard(null));
        Assert.Equal("—", PrintService.MaskIdCard("   "));
    }

    [Fact]
    public void IdCard_ShortValue_IsFullyMasked()
    {
        // 8 位以内全打码：保留首尾反而会暴露大部分内容
        Assert.Equal("****", PrintService.MaskIdCard("AB12"));
        Assert.Equal("********", PrintService.MaskIdCard("12345678"));
    }

    [Fact]
    public void IdCard_MaskedLengthEqualsOriginal()
    {
        // 长度一致便于对账时确认是同一个号，也避免前端按长度判断「是否为空」
        const string raw = "44030519900101123X";
        Assert.Equal(raw.Length, PrintService.MaskIdCard(raw).Length);
    }

    [Fact]
    public void Scrub_ReplacesRegisteredSecrets()
    {
        const string key = "MIIEvQIBADANBgkqhkiG9w0BAQEFAASC";
        var text = $"响应体：{{\"key\":\"{key}\"}}";
        var scrubbed = SecretMasker.Scrub(text, key);

        Assert.DoesNotContain(key, scrubbed);
        Assert.Contains("••••••••", scrubbed);
    }

    [Fact]
    public void Scrub_IgnoresTooShortSecrets()
    {
        // 短串（如 "123"）替换会误伤正常文本，因此少于 8 字符不处理
        Assert.Equal("订单 123 已创建", SecretMasker.Scrub("订单 123 已创建", "123"));
    }

    [Fact]
    public void Scrub_HandlesNullTextAndNullSecrets()
    {
        Assert.Equal("", SecretMasker.Scrub(null, "abcdefghij"));
        Assert.Equal("正常文本", SecretMasker.Scrub("正常文本", null!));
    }

    [Fact]
    public void Truncate_KeepsShortTextIntact()
    {
        Assert.Equal("短文本", SecretMasker.Truncate("短文本", 300));
    }

    [Fact]
    public void Truncate_CutsLongTextAndReportsOriginalLength()
    {
        var longText = new string('A', 500);
        var result = SecretMasker.Truncate(longText, 100);

        Assert.StartsWith(new string('A', 100), result);
        Assert.Contains("共 500 字符", result);
    }
}

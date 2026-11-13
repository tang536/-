using ERental.Services;

namespace ERental.Tests;

/// <summary>入参 XSS 清洗：只剥标签与伪协议，不改变普通文本语义</summary>
public class InputSanitizerTests
{
    [Fact]
    public void Clean_RemovesHtmlTags()
    {
        Assert.Equal("alert(1)", InputSanitizer.Clean("<script>alert(1)</script>"));
        Assert.Equal("你好", InputSanitizer.Clean("<b>你好</b>"));
    }

    [Fact]
    public void Clean_RemovesScriptAndDangerousDataProtocols()
    {
        Assert.Equal("alert(1)", InputSanitizer.Clean("javascript:alert(1)"));
        Assert.Equal(";base64,PHNjcmlwdD4=x", InputSanitizer.Clean("data:text/html;base64,PHNjcmlwdD4=x"));
    }

    [Fact]
    public void Clean_KeepsPlainComparisons()
    {
        const string text = "价格<100 元，押金>50";
        Assert.Same(text, InputSanitizer.Clean(text));
        Assert.Null(InputSanitizer.Clean(null));
        Assert.Equal(string.Empty, InputSanitizer.Clean(string.Empty));
    }

    [Fact]
    public void SanitizeObject_CleansWritableStringPropertiesAndLists()
    {
        var dto = new SanitizeSample
        {
            Name = "<img src=x onerror=alert(1)>张三",
            Remark = "javascript:alert(1)",
            Tags = ["<b>vip</b>", "普通"],
        };

        var changed = InputSanitizer.SanitizeObject(dto);

        Assert.Equal("张三", dto.Name);
        Assert.Equal("alert(1)", dto.Remark);
        Assert.Equal(new[] { "vip", "普通" }, dto.Tags);
        Assert.True(changed >= 2);
    }

    [Fact]
    public void SanitizeObject_SkipsPasswordAndSecretFields()
    {
        var dto = new SanitizeSample
        {
            Password = "<b>p@ss</b>",
            ApiKey = "javascript:key",
        };

        InputSanitizer.SanitizeObject(dto);

        Assert.Equal("<b>p@ss</b>", dto.Password);
        Assert.Equal("javascript:key", dto.ApiKey);
    }

    private sealed class SanitizeSample
    {
        public string? Name { get; set; }
        public string? Remark { get; set; }
        public List<string> Tags { get; set; } = [];
        public string? Password { get; set; }
        public string? ApiKey { get; set; }
    }
}

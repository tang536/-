using System.Reflection;
using System.Text.RegularExpressions;

namespace ERental.Services;

/// <summary>用户输入的 XSS 清洗：剥掉字符串里的 HTML 标签与脚本伪协议</summary>
public static partial class InputSanitizer
{
    /// <summary>HTML 标签：&lt; + 可选空白/斜杠 + 字母或叹号开头，一直到最近的 &gt;</summary>
    [GeneratedRegex(@"<\s*/?\s*[a-zA-Z!][^>]*>")]
    private static partial Regex HtmlTagPattern();

    /// <summary>危险 data 协议：data:text/html 这类会把整段 HTML 当文档渲染的写法（data:image …</summary>
    [GeneratedRegex(@"(?i)\bdata\s*:\s*(?:text/html|application/xhtml\+xml)")]
    private static partial Regex DangerousDataPattern();

    /// <summary>脚本伪协议：javascript: / vbscript:，中间允许夹杂空白绕过</summary>
    [GeneratedRegex(@"(?i)\b(?:javascript|vbscript)\s*:")]
    private static partial Regex ScriptProtocolPattern();

    /// <summary>明确不该清洗的字段名</summary>
    private static readonly string[] SkipHints =
        ["password", "pwd", "secret", "token", "signature", "apikey", "api_key", "privatekey", "private_key"];

    /// <summary>清洗一个字符串</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        // 快速路径：没有 < 也没有 : 的文本绝不可能构成标签或伪协议，不必进正则
        if (!value.Contains('<') && !value.Contains(':')) return value;

        var s = HtmlTagPattern().Replace(value, string.Empty);
        s = DangerousDataPattern().Replace(s, string.Empty);
        s = ScriptProtocolPattern().Replace(s, string.Empty);
        return s;
    }

    /// <summary>清洗一个入参对象上所有「可读写的 string 属性」，返回被改动的字段数</summary>
    public static int SanitizeObject(object? value, int depth = 0)
    {
        if (value is null || depth > 2) return 0;
        if (value is string or IFormFile or Stream) return 0;

        if (value is System.Collections.IEnumerable seq)
        {
            var sum = 0;
            foreach (var item in seq) sum += SanitizeObject(item, depth + 1);
            return sum;
        }

        var type = value.GetType();
        // 基础类型、枚举、框架类型（DateTime/Guid/decimal/字典…）不必也不该反射
        if (type.IsPrimitive || type.IsEnum || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
            return 0;

        var changed = 0;
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
            if (p.PropertyType != typeof(string)) continue;
            if (SkipHints.Any(h => p.Name.Contains(h, StringComparison.OrdinalIgnoreCase))) continue;
            if (p.GetValue(value) is not string current) continue;

            var cleaned = Clean(current);
            if (ReferenceEquals(cleaned, current)) continue;   // 未改动：不碰它
            p.SetValue(value, cleaned);
            changed++;
        }
        return changed;
    }
}

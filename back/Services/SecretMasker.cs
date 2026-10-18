namespace ERental.Services;

/// <summary>
/// 敏感值脱敏工具。**所有对外输出（日志、异常响应、DTO 回显）都必须过这里。**
///
/// 为什么单开一个类：敏感信息泄漏通常是"某处忘了脱敏"，而不是"某处脱敏写错了"。
/// 把脱敏收敛到一个类、一套规则，便于审计——搜 `SecretMasker.` 就能列出全部脱敏点；
/// 新加日志时也能一眼看到该项目里"有个现成的脱敏方法可用"。
///
/// 设计取舍：**不做"智能识别"**（如正则扫 Base64 猜密钥）。
/// 那类规则容易漏判又容易误伤（把正常业务串也打码），排查时反而更痛苦。
/// 这里只提供几个**显式调用**的方法，由写日志/拼 DTO 的人明确声明"这个字段是敏感的"。
/// </summary>
public static class SecretMasker
{
    /// <summary>密钥类值的统一占位符。回显时用它替代真实值，且**长度也一并隐去**（长度同样是有用信息）。</summary>
    private const string Placeholder = "••••••••";

    /// <summary>
    /// 从日志文本中清除已登记为敏感的字面值。
    ///
    /// 用途：回调表单/网关响应这类"整体打进日志"的内容，其中夹带的 sign、密钥片段
    /// 无法逐字段处理，只能拿已知敏感值做字面替换。
    /// 传入的敏感值若太短（&lt; 8 字符）会被忽略——替换短串容易误伤正常文本。
    /// </summary>
    public static string Scrub(string? text, params string?[] secrets)
    {
        var result = text ?? "";
        // 显式传 null 数组（如 Scrub(text, null) 的反射/动态调用）会让 foreach 抛空引用；
        // 脱敏是最后一道防线，宁可什么都不做也不能把调用方带崩。
        if (secrets is null) return result;
        foreach (var s in secrets)
        {
            var v = (s ?? "").Trim();
            if (v.Length < 8) continue;              // 太短，替换会误伤
            result = result.Replace(v, Placeholder, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>
    /// 截断长文本，用于日志与响应体。
    /// 与脱敏配合使用：先截断再打日志，避免把整段网关响应（可能含 token）写进日志文件。
    /// </summary>
    public static string Truncate(string? text, int max = 300)
    {
        var t = text ?? "";
        return t.Length <= max ? t : t[..max] + $"…（共 {t.Length} 字符，已截断）";
    }
}

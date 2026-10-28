using System.Reflection;
using System.Text.RegularExpressions;

namespace ERental.Services;

/// <summary>
/// 用户输入的 XSS 清洗：剥掉字符串里的 HTML 标签与脚本伪协议。
///
/// ── 为什么后端也要做（前端明明已经转义了）──
/// 前端 Vue 的 <c>{{ }}</c> 插值默认转义，全项目也确实没有一处 <c>v-html</c>，
/// 所以「当前这版界面」不存在反射型 XSS。但存储型 XSS 的防线不能只靠消费端自觉：
///   ① 备注、工单描述、意见反馈这类自由文本会被导出成 CSV、被第三方报表工具读取，
///      将来也可能被某个新页面用 v-html 渲染——一次疏忽就穿透；
///   ② 同一套接口还会被小程序、移动端、开放平台调用，这些客户端不保证转义；
///   ③ 数据库一旦存进 <c>&lt;script&gt;</c>，每个下游消费者都得各自防一遍，
///      成本远高于在入口处一次清干净。
/// 在「入口」统一清洗是覆盖最全、成本最低的位置。
///
/// ── 为什么是「剥离标签」而不是「转义所有尖括号」──
/// 转义会把「价格&lt;100元」存成「价格&amp;lt;100元」，前端按文本渲染时用户看到的是
/// 「&amp;lt;」而不是「&lt;」——为安全把一句正常备注改成乱码，属于用正确性换安全感。
/// 这里只在**确实构成 HTML 标签**时才剥离（<c>&lt;</c> 后紧跟字母或 <c>!</c>），
/// 「价格&lt;100」「A&gt;B」这类普通文本原样保留。
///
/// ── 为什么不做成「拦截并报错」──
/// 柜台的备注里出现尖括号是常态（「胎压&lt;1.5 需补气」）。直接拒绝会把正常业务卡死，
/// 而清洗后的语义仍然完整（只少了标签本身）。真正需要拦的是攻击载荷，不是尖括号。
/// </summary>
public static partial class InputSanitizer
{
    /// <summary>
    /// HTML 标签：<c>&lt;</c> + 可选空白/斜杠 + 字母或叹号开头，一直到最近的 <c>&gt;</c>。
    /// <c>[^&gt;]*</c> 保证不会跨过标签边界吃掉后面的正常文本。
    /// </summary>
    [GeneratedRegex(@"<\s*/?\s*[a-zA-Z!][^>]*>")]
    private static partial Regex HtmlTagPattern();

    /// <summary>危险 data 协议：<c>data:text/html</c> 这类会把整段 HTML 当文档渲染的写法（data:image 不在此列，图片上传要用）</summary>
    [GeneratedRegex(@"(?i)\bdata\s*:\s*(?:text/html|application/xhtml\+xml)")]
    private static partial Regex DangerousDataPattern();

    /// <summary>脚本伪协议：<c>javascript:</c> / <c>vbscript:</c>，中间允许夹杂空白绕过</summary>
    [GeneratedRegex(@"(?i)\b(?:javascript|vbscript)\s*:")]
    private static partial Regex ScriptProtocolPattern();

    /// <summary>
    /// 明确不该清洗的字段名。
    /// 密码、密钥、签名里的 <c>&lt; &gt; :</c> 是合法字符，剥掉会让人直接登不进来，
    /// 且这些值不会被当 HTML 渲染，没有 XSS 面。
    /// </summary>
    private static readonly string[] SkipHints =
        ["password", "pwd", "secret", "token", "signature", "apikey", "api_key", "privatekey", "private_key"];

    /// <summary>
    /// 清洗一个字符串。无危险内容时**原样返回同一个引用**——
    /// 调用方可据此零成本判断「有没有发生改动」，也避免给绝大多数正常字段分配新字符串。
    /// </summary>
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

    /// <summary>
    /// 清洗一个入参对象上所有「可读写的 string 属性」，返回被改动的字段数。
    ///
    /// 只向下走两层（对象 → 集合元素），不做深度递归：DTO 的层级就这么深，
    /// 再往下递归会顺着导航属性摸到整个 EF 实体图，既慢又可能把不该动的字段改掉。
    /// </summary>
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

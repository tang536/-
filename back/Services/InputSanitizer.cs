using System.Reflection;
using System.Text.RegularExpressions;

namespace ERental.Services;

///<summary>用户输入的 XSS 清洗：剥掉字符串里的 HTML 标签与脚本伪协议</summary>
internal static partial class InputSanitizer
{
    ///<summary>HTML 标签：＆lt</summary>
    [GeneratedRegex(@"<\s*/?\s*[a-zA-Z!][^>]*>")]
    private static partial Regex HtmlTagPattern();

    ///<summary>危险 data 协议：data:text/html 这类会把整段 HTML 当文档渲染的写法</summary>
    [GeneratedRegex(@"(?i)\bdata\s*:\s*(?:text/html|application/xhtml\+xml)")]
    private static partial Regex DangerousDataPattern();

    ///<summary>脚本伪协议</summary>
    [GeneratedRegex(@"(?i)\b(?:javascript|vbscript)\s*:")]
    private static partial Regex ScriptProtocolPattern();

    ///<summary>明确不该清洗的字段名</summary>
    private static readonly string[] SkipHints = ["password", "pwd", "secret", "token", "signature", "apikey", "api_key", "privatekey", "private_key"];

    ///<summary>递归上限</summary>
    private const int MaxDepth = 2;

    ///<summary>清洗一个字符串</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        //快速路径：没有<也没有:的文本绝不可能构成标签或伪协议，不必进正则
        if (!value.Contains('<') && !value.Contains(':')) return value;

        var s = HtmlTagPattern().Replace(value, string.Empty);
        s = DangerousDataPattern().Replace(s, string.Empty);
        s = ScriptProtocolPattern().Replace(s, string.Empty);
        return s;
    }

    ///<summary>清洗一个入参对象上所有「可读写的 string 属性」以及集合里的 string 元素</summary>
    public static int SanitizeObject(object? value, int depth = 0)
    {
        //这里只留「该不该处理+分成三类（集合/普通对象）」，具体怎么清洗交给下面的私有方法
        if (!ShouldInspect(value, depth)) return 0;

        //ShouldInspect 已排除 null，这里 value 必不为空
        return value is System.Collections.IEnumerable seq
            ? SanitizeSequence(value, seq, depth)
            : SanitizeProperties(value!);
    }

    ///<summary>判断这个值是否需要清洗</summary>
    private static bool ShouldInspect(object? value, int depth)
    {
        if (value is null || depth > MaxDepth) return false;
        return value is not string and not IFormFile and not Stream;
    }

    ///<summary>集合形态的分发：字典/字符串列表要就地写回，其它只是逐个元素递归</summary>
    private static int SanitizeSequence(object value, System.Collections.IEnumerable seq, int depth)
    {
        //顶层就是字典：string 值不可变，用键索引器把清洗结果写回原字典（键是业务标识，不动）
        if (value is IDictionary<string, string> topDict) return SanitizeStringDictionary(topDict);

        //顶层就是 List<string> / string[]：字符串不可变
        if (value is IList<string> strings) return SanitizeStringList(strings);

        var sum = 0;
        foreach (var item in seq) sum += SanitizeObject(item, depth + 1);
        return sum;
    }

    ///<summary>就地清洗字典的 string 值（键不动），返回改动处数</summary>
    private static int SanitizeStringDictionary(IDictionary<string, string> dict)
    {
        var replaced = 0;
        //Keys.ToList
        foreach (var key in dict.Keys.ToList())
        {
            var raw = dict[key];
            var cleaned = Clean(raw);
            if (ReferenceEquals(cleaned, raw)) continue;//未改动：不写回，避免无谓的集合变更通知
            dict[key] = cleaned!;
            replaced++;
        }
        return replaced;
    }

    ///<summary>就地替换字符串列表里的元素，返回改动处数</summary>
    private static int SanitizeStringList(IList<string> strings)
    {
        var replaced = 0;
        for (var i = 0; i < strings.Count; i++)
        {
            var raw = strings[i];
            var cleaned = Clean(raw);
            if (ReferenceEquals(cleaned, raw)) continue;//未改动：不写回
            strings[i] = cleaned!;
            replaced++;
        }
        return replaced;
    }

    ///<summary>反射遍历对象属性并逐项清洗，返回改动处数</summary>
    private static int SanitizeProperties(object value)
    {
        var type = value.GetType();
        if (IsFrameworkType(type)) return 0;

        var changed = 0;
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            changed += SanitizeProperty(value, p);
        return changed;
    }

    ///<summary>基础类型、枚举、框架类型（DateTime/Guid/decimal…）不必也不该反射</summary>
    private static bool IsFrameworkType(Type type)
        => type.IsPrimitive
           || type.IsEnum
           || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true;

    ///<summary>单个属性的清洗：string 走直接写回，其余交给集合处理</summary>
    private static int SanitizeProperty(object owner, PropertyInfo p)
    {
        //只读属性和索引器无法 SetValue，直接跳过（索引器还要参数，强行读写会抛异常）
        if (!p.CanRead || p.GetIndexParameters().Length > 0) return 0;
        if (IsSkippedProperty(p)) return 0;

        //集合属性：List<string> 元素、字典的 string 值同样来自用户输入
        return p.PropertyType == typeof(string)
            ? SanitizeStringProperty(owner, p)
            : CleanCollectionProperty(owner, p);
    }

    ///<summary>密码/密钥类字段按名跳过：清洗会把其中的 ＆lt</summary>
    private static bool IsSkippedProperty(PropertyInfo p)
        => SkipHints.Any(h => p.Name.Contains(h, StringComparison.OrdinalIgnoreCase));

    ///<summary>清洗单个 string 属性并写回，返回改动处数（0 或 1）</summary>
    private static int SanitizeStringProperty(object owner, PropertyInfo p)
    {
        if (!p.CanWrite || p.GetValue(owner) is not string current) return 0;
        var cleaned = Clean(current);
        if (ReferenceEquals(cleaned, current)) return 0;//未改动：不碰它
        p.SetValue(owner, cleaned);
        return 1;
    }

    ///<summary>重建可写集合属性，把里面的 string 元素/字典值清洗后写回</summary>
    private static int CleanCollectionProperty(object owner, PropertyInfo p)
    {
        if (!p.CanWrite) return 0;
        var current = p.GetValue(owner);

        //按原有判断顺序分发：先字符串列表，后字典
        return current switch
        {
            IList<string> list => CleanStringListProperty(owner, p, list),
            IDictionary<string, string> dict => CleanStringDictionaryProperty(owner, p, dict),
            _ => 0
        };
    }

    ///<summary>重建字符串列表属性并整体写回，返回改动处数</summary>
    private static int CleanStringListProperty(object owner, PropertyInfo p, IList<string> list)
    {
        //List<string>（含 IList/ICollection/IEnumerable<string> 声明、以及 string[]）：string 不可变
        var changed = 0;
        var rebuilt = new List<string>(list.Count);
        foreach (var s in list)
        {
            var cleaned = Clean(s);
            if (!ReferenceEquals(cleaned, s)) changed++;
            rebuilt.Add(cleaned!);
        }
        if (changed == 0) return 0;//没改动就不写回，保留调用方原有的集合实例
        object replacement = p.PropertyType == typeof(string[]) ? rebuilt.ToArray() : rebuilt;
        //属性类型接不住新集合（如 HashSet<string>）时跳过
        if (!p.PropertyType.IsInstanceOfType(replacement)) return 0;
        p.SetValue(owner, replacement);
        return changed;
    }

    ///<summary>重建字典属性并整体写回，返回改动处数</summary>
    private static int CleanStringDictionaryProperty(object owner, PropertyInfo p, IDictionary<string, string> dict)
    {
        //Dictionary<string,string>：键是业务标识不动，只清洗值
        var comparer = dict is Dictionary<string, string> concrete ? concrete.Comparer : StringComparer.Ordinal;
        var rebuilt = new Dictionary<string, string>(dict.Count, comparer);
        var changed = 0;
        foreach (var kv in dict)
        {
            var cleaned = Clean(kv.Value);
            if (!ReferenceEquals(cleaned, kv.Value)) changed++;
            rebuilt[kv.Key] = cleaned!;
        }
        if (changed == 0 || !p.PropertyType.IsInstanceOfType(rebuilt)) return 0;
        p.SetValue(owner, rebuilt);
        return changed;
    }
}

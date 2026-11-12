namespace ERental.Services;

///<summary>敏感值脱敏工具</summary>
internal static class SecretMasker
{
    ///<summary>密钥类值的统一占位符</summary>
    private const string Placeholder="••••••••";

    ///<summary>从日志文本中清除已登记为敏感的字面值</summary>
    public static string Scrub(string? text,params string?[] secrets)
    {
        var result=text??"";
        //显式传 null 数组 的反射/动态调用）会让 foreach 抛空引用
        if(secrets is null) return result;
        foreach(var s in secrets)
        {
            var v=(s??"").Trim();
            if(v.Length<8) continue;//太短，替换会误伤
            result=result.Replace(v,Placeholder,StringComparison.Ordinal);
        }
        return result;
    }

    ///<summary>截断长文本，用于日志与响应体</summary>
    public static string Truncate(string? text,int max=300)
    {
        var t=text??"";
        return t.Length<=max?t:t[..max]+$"…（共 {t.Length} 字符，已截断）";
    }
}

namespace ERental.Services;

///<summary>敏感配置的分层读取器：环境变量 → 用户机密（User Secrets） → 运行时数据源（数据库）</summary>
public sealed class SecretProvider(IConfiguration config)
{
    private readonly IConfiguration _config = config;

    ///<summary>按优先级解析一个敏感项</summary>
    public string Resolve(string key, string? dbValue, out string source)
    {
        //① 环境变量（最高优先级）：生产部署用，注入后数据库那份自动失效
        var envValue = Environment.GetEnvironmentVariable(ToEnvName(key))
                       ?? Environment.GetEnvironmentVariable(ToLegacyEnvName(key));
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            source = "env";
            return envValue.Trim();
        }

        //② 用户机密/appsettings 的 Pay 节点（次高）
        var confValue = _config[key.Replace('.', ':')] ?? _config[key];
        if (!string.IsNullOrWhiteSpace(confValue))
        {
            source = "secrets";
            return confValue.Trim();
        }

        //③ 数据库（兜底）：现有"管理员在页面维护凭证"的能力，继续保持可用
        var v = (dbValue ?? "").Trim();
        source = v.Length > 0 ? "db" : "none";
        return v;
    }

    ///<summary>外部键 → 环境变量名：层级分隔符 . /:一次性映射为双下划线，键名自带的下划线原样保留</summary>
    private static string ToEnvName(string key) => key.Replace(".", "__").Replace(":", "__").ToUpperInvariant();

    ///<summary>历史规则：先把 . /:换成下划线、再把所有下划线翻倍（键名自带的下划线也被翻倍）</summary>
    private static string ToLegacyEnvName(string key) => key.Replace('.', '_').Replace(':', '_').Replace("_", "__").ToUpperInvariant();
}

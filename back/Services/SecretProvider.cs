namespace ERental.Services;

/// <summary>敏感配置的分层读取器：环境变量 → 用户机密（User Secrets） → 运行时数据源（数据库）</summary>
public class SecretProvider(IConfiguration config)
{
    private readonly IConfiguration _config = config;

    /// <summary>按优先级解析一个敏感项</summary>
    public string Resolve(string key, string? dbValue, out string source)
    {
        // ① 环境变量（最高优先级）：生产部署用，注入后数据库那份自动失效
        var envName = ToEnvName(key);
        var envValue = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            source = "env";
            return envValue.Trim();
        }

        // ② 用户机密 / appsettings 的 Pay 节点（次高）：本地开发不想往库里塞密钥时用
        //    注意：这里读的是 IConfiguration，其内容取决于启动时的配置源，
        //    因此 appsettings.json 里**不应**再出现密钥（已清理），只留用户机密这一条路。
        var confValue = _config[key.Replace('.', ':')] ?? _config[key];
        if (!string.IsNullOrWhiteSpace(confValue))
        {
            source = "secrets";
            return confValue.Trim();
        }

        // ③ 数据库（兜底）：现有"管理员在页面维护凭证"的能力，继续保持可用
        var v = (dbValue ?? "").Trim();
        source = v.Length > 0 ? "db" : "none";
        return v;
    }

    /// <summary>外部键 → 环境变量名</summary>
    private static string ToEnvName(string key) =>
        key.Replace('.', '_').Replace(':', '_').Replace("_", "__").ToUpperInvariant();
}

namespace ERental.Services;

/// <summary>
/// 敏感配置的**分层读取器**：环境变量 → 用户机密（User Secrets） → 运行时数据源（数据库）。
///
/// ── 为什么要分层 ──
/// 本项目有两种互相冲突的需求：
///   ① 课设场景：管理员要在页面上直接填/改支付凭证（必须能落到数据库，且改了立即生效，不重启）；
///   ② 生产场景：密钥不该躺在数据库里，应由部署环境注入（环境变量 / 挂载的密钥文件 / KMS）。
/// 两者不是二选一——**高优先级源存在时覆盖低优先级源**即可同时满足：
/// 开发期只填数据库就能跑；上线时在环境变量里注入同名值，数据库那份自动失效。
///
/// ── 键名映射（外部键 → 环境变量名）──
/// 外部沿用数据库里的键名（`pay.sandbox_private_key` 这种），转成环境变量时
/// 走 .NET 标准约定：`:` 与 `.` 都换成 `__`。因此：
///   pay.sandbox_private_key  →  PAY__SANDBOX__PRIVATE_KEY
///   pay.prod_appid           →  PAY__PROD__APPID
/// 这样无需记忆额外规则，与 ASP.NET Core 本身的配置约定一致。
///
/// ── 安全约束 ──
/// 本类**只返回值，不写日志**。密钥绝不进日志、不进异常消息、不进接口响应——
/// 唯一出口是 <see cref="Resolve"/> 的返回值，由调用方在内存中使用。
/// </summary>
public class SecretProvider(IConfiguration config)
{
    private readonly IConfiguration _config = config;

    /// <summary>
    /// 按优先级解析一个敏感项。命中哪一层用 <paramref name="source"/> 回传，便于后台排查"值到底从哪来"（不暴露值本身）。
    /// </summary>
    /// <param name="key">外部键名，如 <c>pay.sandbox_private_key</c></param>
    /// <param name="dbValue">数据库（最低优先级）中的值</param>
    /// <param name="source">返回命中来源：env / secrets / db / none</param>
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

    /// <summary>
    /// 外部键 → 环境变量名。`.` 与 `:` 一律转 `__`（.NET 配置系统的标准约定），整体转大写。
    /// 例：<c>pay.prod_appid</c> → <c>PAY__PROD__APPID</c>
    /// </summary>
    private static string ToEnvName(string key) =>
        key.Replace('.', '_').Replace(':', '_').Replace("_", "__").ToUpperInvariant();
}

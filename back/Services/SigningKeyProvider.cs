using System.Security.Cryptography;
using System.Text;

namespace ERental.Services;

/// <summary>
/// 启动时决定 JWT 签名密钥，取值顺序：
///   1) 显式配置（环境变量 <c>Jwt__Key</c> 或 appsettings 里的 <c>Jwt:Key</c>），长度 ≥ 32 且不是仓库里的示例值；
///   2) <c>Jwt:KeyFile</c> 指向的密钥文件（默认 <c>App_Data/signing.key</c>）；
///   3) 都没有：生成 48 字节随机密钥写进该文件，之后每次启动复用。
///
/// 为什么不每次启动都随机：签名密钥一换，所有已签发令牌立即失效；备份密钥
/// （未配置 <c>Backup:Key</c> 时由 Jwt:Key 派生）也会跟着变，历史备份直接解不开。
/// 为什么不再把密钥写死进配置：写死的默认密钥等于公开的密钥，任何人都能伪造管理员令牌。
/// </summary>
public static class SigningKeyProvider
{
    /// <summary>HS256 要求密钥至少 256 位；这里按字符数兜底，48 字节 Base64 后是 64 字符。</summary>
    public const int MinLength = 32;

    private const int SecretBytes = 48;

    /// <summary>仓库历史上出现过的默认值 / 模板占位符，命中即当作「没配」，重新随机。</summary>
    private static readonly string[] InsecureMarkers = { "ChangeMe", "DoNotUseInProduction", "your-secret", "<" };

    /// <summary>
    /// 解析出可用的签名密钥。<paramref name="source"/> 回传来源描述，用于启动日志
    /// （只记来源，绝不打印密钥本身）。
    /// </summary>
    public static string Resolve(IConfiguration cfg, IHostEnvironment env, out string source)
    {
        var configured = cfg["Jwt:Key"];
        if (IsAcceptable(configured))
        {
            source = "配置项 Jwt:Key";
            return configured!.Trim();
        }

        var path = cfg["Jwt:KeyFile"];
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(env.ContentRootPath, "App_Data", "signing.key");
        path = Path.GetFullPath(path!);

        if (File.Exists(path))
        {
            var existing = ReadKeyFile(path);
            if (IsAcceptable(existing))
            {
                source = "密钥文件 " + path;
                return existing!;
            }
        }

        var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            // CreateNew：多实例同时冷启动时只有一个能抢先写入，其余读回它写下的那把，避免两套密钥打架
            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(fs, new UTF8Encoding(false));
            writer.Write(generated);

            // 只有属主可读，别让同机其他账号顺手拷走
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            source = "新生成的密钥文件 " + path;
        }
        catch (IOException)
        {
            generated = ReadKeyFile(path) ?? throw new InvalidOperationException(
                $"密钥文件 {path} 已存在但内容不可用（长度不足 {MinLength}）。请删除它后重启，或显式配置 Jwt:Key。");
            source = "密钥文件 " + path;
        }

        return generated;
    }

    private static string? ReadKeyFile(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool IsAcceptable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        if (value.Length < MinLength) return false;
        foreach (var marker in InsecureMarkers)
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }
}

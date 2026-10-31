using System.Security.Cryptography;
using System.Text;

namespace ERental.Services;

/// <summary>启动时决定 JWT 签名密钥，取值顺序： 1) 显式配置（环境变量 Jwt__Key 或 appsettings 里…</summary>
public static class SigningKeyProvider
{
    /// <summary>HS256 要求密钥至少 256 位</summary>
    public const int MinLength = 32;

    private const int SecretBytes = 48;

    /// <summary>仓库历史上出现过的默认值 / 模板占位符，命中即当作「没配」，重新随机</summary>
    private static readonly string[] InsecureMarkers = { "ChangeMe", "DoNotUseInProduction", "your-secret", "<" };

    /// <summary>解析出可用的签名密钥</summary>
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

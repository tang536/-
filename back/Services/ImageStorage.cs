using ERental.Data;

namespace ERental.Services;

///<summary>图片落盘的统一入口（维保现场照片、车辆图片共用一套规则）</summary>
public class ImageStorage(IWebHostEnvironment env)
{
    private readonly IWebHostEnvironment _env = env;

    private static readonly string[] AllowedImageExt = [".jpg", ".jpeg", ".png", ".webp", ".gif"];
    private const long MaxImageBytes = 5 * 1024 * 1024;

    ///<summary>上传根目录（…/wwwroot/uploads）</summary>
    private string RootUploadsPath
    {
        get
        {
            //未启用静态文件中间件时 WebRootPath 可能为空串
            var root = _env.WebRootPath;
            if (root.Length == 0) root = Path.Combine(_env.ContentRootPath, "wwwroot");
            return Path.Combine(root, "uploads");
        }
    }

    ///<summary>保存图片并返回可直接引用的相对路径</summary>
    public async Task<string> SaveAsync(IFormFile? file, string subDir) => await SaveAsync(file, subDir, null);

    ///<summary>
    /// 保存图片并返回可引用的相对路径。
    ///
    /// 命名规则从「时间戳+随机」升级为「前缀_时间戳_随机」，且按**年月分目录**：
    /// 验车、维保这类证据图会越攒越多（一天几十张），全平铺在一个目录里，
    /// 备份、迁移、清理都要 `ls` 上万行；按月分桶后，找「上个月的还车照」是点目录而不是翻列表。
    /// 前缀由调用方给（例如 Handover_197_还车），文件名自带业务线索，出事时不用反查数据库。
    ///</summary>
    public async Task<string> SaveAsync(IFormFile? file, string subDir, string? namePrefix)
    {
        if (file == null || file.Length == 0) throw new BusinessException("请选择要上传的图片");
        if (file.Length > MaxImageBytes) throw new BusinessException("图片不能超过 5MB，请压缩后再上传");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedImageExt.Contains(ext)) throw new BusinessException("仅支持 jpg / jpeg / png / webp / gif 格式的图片");

        //按年月分桶：老文件留在原目录照样能访问，新文件进新目录
        var bucket = AppClock.Now.ToString("yyyy-MM");
        var rel = $"{subDir}/{bucket}";
        var dir = Path.Combine(RootUploadsPath, subDir, bucket);
        Directory.CreateDirectory(dir);

        var prefix = SanitizePrefix(namePrefix);
        var name = $"{prefix}{AppClock.Now:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}{ext}";
        await using (var fs = new FileStream(Path.Combine(dir, name), FileMode.Create))
        {
            await file.CopyToAsync(fs);
        }
        return $"/uploads/{rel}/{name}";
    }

    ///<summary>前缀只允许字母/数字/汉字/下划线/连字符，避免文件名里出现路径分隔符或空格</summary>
    private static string SanitizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return string.Empty;
        var sb = new System.Text.StringBuilder(prefix.Length + 1);
        foreach (var ch in prefix.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-') sb.Append(ch);
        }
        return sb.Length == 0 ? string.Empty : sb.ToString() + "_";
    }

    ///<summary>保存内存中的图片字节流（AI 生图成品下载后走这里，与上传同一套命名/目录规则）</summary>
    public async Task<string> SaveStreamAsync(Stream content, string subDir, string ext)
    {
        if (!AllowedImageExt.Contains(ext.ToLowerInvariant())) throw new BusinessException("仅支持 jpg / jpeg / png / webp / gif 格式的图片");

        var dir = Path.Combine(RootUploadsPath, subDir);
        Directory.CreateDirectory(dir);

        var name = $"{AppClock.Now:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}{ext.ToLowerInvariant()}";
        var path = Path.Combine(dir, name);
        await using (var fs = new FileStream(path, FileMode.Create))
        {
            content.Seek(0, SeekOrigin.Begin);
            await content.CopyToAsync(fs);
        }
        return $"/uploads/{subDir}/{name}";
    }

    ///<summary>删除一张已上传的图片（按 /uploads/ 开头的相对路径）</summary>
    public void Delete(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase)) return;
        var rel = url["/uploads/".Length..].Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(RootUploadsPath, rel));
        //防路径穿越：最终路径必须在 uploads 根目录内
        if (!full.StartsWith(RootUploadsPath, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(full))
        {
            try { File.Delete(full); } catch {/* 占用中或权限不足时忽略，下次清理再删 */}
        }
    }
}

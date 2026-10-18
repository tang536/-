namespace ERental.Services;

/// <summary>
/// 图片落盘的统一入口（维保现场照片、车辆图片共用一套规则）
/// </summary>
public class ImageStorage(IWebHostEnvironment env)
{
    private readonly IWebHostEnvironment _env = env;

    private static readonly string[] AllowedImageExt = [".jpg", ".jpeg", ".png", ".webp", ".gif"];
    private const long MaxImageBytes = 5 * 1024 * 1024;

    /// <summary>
    /// 上传根目录（…/wwwroot/uploads）。对外暴露是为了让维保照片上传等
    /// 非 HTTP 场景也能把文件写进同一个命名空间，避免两处各拼一次路径、
    /// 日后 wwwroot 位置一变就漏改。
    /// </summary>
    private string RootUploadsPath
    {
        get
        {
            // 未启用静态文件中间件时 WebRootPath 可能为空串，此时回落到 ContentRoot/wwwroot
            var root = _env.WebRootPath;
            if (root.Length == 0) root = Path.Combine(_env.ContentRootPath, "wwwroot");
            return Path.Combine(root, "uploads");
        }
    }

    /// <summary>
    /// 保存图片并返回可直接引用的相对路径（如 /uploads/vehicle/xxx.jpg）。
    /// 文件名用「时间戳 + 随机串」重命名，不采用上传时的原始文件名（避免路径穿越与同名覆盖）。
    /// </summary>
    public async Task<string> SaveAsync(IFormFile? file, string subDir)
    {
        if (file == null || file.Length == 0) throw new Exception("请选择要上传的图片");
        if (file.Length > MaxImageBytes) throw new Exception("图片不能超过 5MB，请压缩后再上传");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedImageExt.Contains(ext))
            throw new Exception("仅支持 jpg / jpeg / png / webp / gif 格式的图片");

        var dir = Path.Combine(RootUploadsPath, subDir);
        Directory.CreateDirectory(dir);

        var name = $"{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}{ext}";
        await using (var fs = new FileStream(Path.Combine(dir, name), FileMode.Create))
        {
            await file.CopyToAsync(fs);
        }
        return $"/uploads/{subDir}/{name}";
    }

    /// <summary>
    /// 保存内存中的图片字节流（AI 生图成品下载后走这里，与上传同一套命名/目录规则）。
    /// ext 需带点（如 ".png"）。返回可直接引用的相对路径。
    /// </summary>
    public async Task<string> SaveStreamAsync(Stream content, string subDir, string ext)
    {
        if (!AllowedImageExt.Contains(ext.ToLowerInvariant()))
            throw new Exception("仅支持 jpg / jpeg / png / webp / gif 格式的图片");

        var dir = Path.Combine(RootUploadsPath, subDir);
        Directory.CreateDirectory(dir);

        var name = $"{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}{ext.ToLowerInvariant()}";
        var path = Path.Combine(dir, name);
        await using (var fs = new FileStream(path, FileMode.Create))
        {
            content.Seek(0, SeekOrigin.Begin);
            await content.CopyToAsync(fs);
        }
        return $"/uploads/{subDir}/{name}";
    }

    /// <summary>
    /// 删除一张已上传的图片（按 /uploads/ 开头的相对路径）。
    /// 文件不存在时静默忽略；路径不在 uploads 目录下则拒绝删除。
    /// </summary>
    public void Delete(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase)) return;
        var rel = url["/uploads/".Length..].Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(RootUploadsPath, rel));
        // 防路径穿越：最终路径必须在 uploads 根目录内
        if (!full.StartsWith(RootUploadsPath, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(full))
        {
            try { File.Delete(full); } catch { /* 占用中或权限不足时忽略，下次清理再删 */ }
        }
    }
}

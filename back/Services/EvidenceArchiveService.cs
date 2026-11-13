using ERental.Data;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>
/// 证据照片归档（验车照 / 维保现场照）。
///
/// 为什么要做：这两类照片只会越攒越多，而它们的用途全都集中在「最近几个月」——
/// 三个月后的还车纠纷基本靠结算单和小票，照片只是备查。全留在 uploads 根下，
/// 目录会膨胀到上万文件，备份变慢、迁移变慢，连带前端列目录都吃力。
///
/// 做法：超过 <c>upload_archive_days</c> 的文件移入 <c>uploads/_archive/yyyy-MM/原分类/</c>，
/// **文件名与访问路径同步回写数据库**，所以前端拿到的 URL 依旧能打开（只是换了目录）。
/// 迁移用「移动 + 回写」而不是「按原路径继续索引」，是因为一旦搬了目录，
/// 库里若还留旧路径，验车记录就会出现一排打不开的红叉，比不归档更糟。
/// </summary>
public sealed class EvidenceArchiveService(AppDbContext db, IWebHostEnvironment env, ILogger<EvidenceArchiveService> logger)
{
    ///<summary>归档保留期配置键（天；0 表示不归档）</summary>
    private const string KeyArchiveDays = "upload_archive_days";

    ///<summary>参与归档的分类目录（都是证据图；车辆图、头像属在册资料，不搬）</summary>
    private static readonly string[] EvidenceDirs = ["handover", "maintenance"];

    private readonly AppDbContext _db = db;
    private readonly IWebHostEnvironment _env = env;
    private readonly ILogger<EvidenceArchiveService> _logger = logger;

    private string RootUploadsPath
    {
        get
        {
            var root = _env.WebRootPath;
            if (root.Length == 0) root = Path.Combine(_env.ContentRootPath, "wwwroot");
            return Path.Combine(root, "uploads");
        }
    }

    ///<summary>执行一次归档，返回移动的文件数（启动时调用；失败不影响启动）</summary>
    public int ArchiveOnce()
    {
        var days = int.TryParse(
            _db.SystemConfigs.FirstOrDefault(c => c.ConfigKey == KeyArchiveDays)?.ConfigValue, out var v) ? v : 180;
        if (days <= 0) return 0;

        var cutoff = AppClock.Now.AddDays(-days);
        var moved = 0;

        foreach (var sub in EvidenceDirs)
        {
            var srcRoot = Path.Combine(RootUploadsPath, sub);
            if (!Directory.Exists(srcRoot)) continue;

            //新布局是「分类/yyyy-MM/」，老文件直接躺在「分类/」下，两种都要覆盖
            foreach (var file in Directory.EnumerateFiles(srcRoot, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime >= cutoff) continue;

                //按文件的修改时间分桶，而不是按今天：归档目录要和照片所属的月份对得上
                var month = info.LastWriteTime.ToString("yyyy-MM");
                var destDir = Path.Combine(RootUploadsPath, "_archive", month, sub);
                Directory.CreateDirectory(destDir);

                var relOld = RelativeUrl(file);
                var destName = info.Name;
                var dest = Path.Combine(destDir, destName);
                //同名兜底：归档目录里已存在就不覆盖，加序号（宁可多一个副本，也不丢证据）
                var n = 1;
                while (File.Exists(dest))
                {
                    destName = $"{Path.GetFileNameWithoutExtension(info.Name)}_{n}{info.Extension}";
                    dest = Path.Combine(destDir, destName);
                    n++;
                }

                try
                {
                    File.Move(file, dest);
                }
                catch (Exception ex)
                {
                    //文件被占用（正在被查看/杀软扫描）时跳过，下一次启动再处理
                    _logger.LogWarning(ex, "照片归档跳过 {File}", info.FullName);
                    continue;
                }

                var relNew = $"/uploads/_archive/{month}/{sub}/{destName}";
                RewritePaths(relOld, relNew);
                moved++;
            }
        }

        if (moved > 0) _logger.LogInformation("证据照片归档：移动 {Count} 张（保留 {Days} 天）", moved, days);
        return moved;
    }

    ///<summary>物理路径 → /uploads/ 开头的访问路径</summary>
    private string RelativeUrl(string fullPath)
    {
        var rel = Path.GetRelativePath(RootUploadsPath, fullPath).Replace('\\', '/');
        return $"/uploads/{rel}";
    }

    ///<summary>
    /// 把库里引用旧路径的字段改成新路径。
    /// 用 SQL REPLACE 而不是实体更新：涉及两张表、字段是逗号拼接的多图串，
    /// 实体读改写要自己拆串拼串，一旦中间有脏数据（比如空格）就会拼坏整串。
    /// </summary>
    private void RewritePaths(string oldUrl, string newUrl)
    {
        //MySQL REPLACE 区分大小写与空格：旧串按库里存的原文精确匹配
        _db.Database.ExecuteSqlRaw(
            "UPDATE order_handover SET photo_paths = REPLACE(photo_paths, {0}, {1}) WHERE photo_paths LIKE {2}",
            oldUrl, newUrl, $"%{oldUrl}%");
        _db.Database.ExecuteSqlRaw(
            "UPDATE maintenance_order SET fault_image = REPLACE(fault_image, {0}, {1}) WHERE fault_image LIKE {2}",
            oldUrl, newUrl, $"%{oldUrl}%");
    }
}

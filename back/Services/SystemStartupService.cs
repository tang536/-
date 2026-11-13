using ERental.Data;

namespace ERental.Services;

///<summary>三步都是幂等的：EnsureCreated 只在库不存在时建表</summary>
public sealed class SystemStartupService(
    AppDbContext db,
    IConfiguration config,
    EvidenceArchiveService archive,
    ILogger<SystemStartupService> logger)
{
    ///<summary>执行启动序列，返回本次启动后的规模快照（用于日志与健康检查）</summary>
    public StartupReport Startup()
    {
        var tableCount = db.Model.GetEntityTypes().Count();

        DbInitializer.EnsureDatabase(db);
        // 连接建立后先确认会话时区：视图里的 NOW() 与服务层的 AppClock.Now 必须是同一口径，
        // 否则逾期判定与计费小时数会在列表和结算两处给出不同结果。
        DatabaseClock.EnsureSessionMatchesLocalTime(db);
        DbInitializer.EnsureSchemaUpgrades(db);
        DbInitializer.Seed(db, config);

        // 证据照片归档放在启动末尾做一次：启动流程本身不能因为一次文件移动失败而起不来，
        // 所以这里整体兜住异常，失败只记 warning，下次启动再补。
        try
        {
            archive.ArchiveOnce();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "证据照片归档失败，已跳过（不影响启动）");
        }

        var report = new StartupReport(
            TableCount: tableCount,
            ConfigCount: db.SystemConfigs.Count(),
            AccountCount: db.SystemUsers.Count(),
            MemberLevelCount: db.MemberRights.Count());

        logger.LogInformation("startup() 完成：{Tables} 个实体映射就绪，配置项 {Configs} 条，账号 {Accounts} 个，会员等级 {Levels} 档", report.TableCount, report.ConfigCount, report.AccountCount, report.MemberLevelCount);

        return report;
    }
}

///<summary>启动序列的输出：本次启动后系统应具备的规模基线</summary>
public sealed record StartupReport(int TableCount, int ConfigCount, int AccountCount, int MemberLevelCount);

using ERental.Data;

namespace ERental.Services;

/// <summary>
/// 三步都是幂等的：<c>EnsureCreated</c> 只在库不存在时建表，<c>EnsureSchemaUpgrades</c> 按索引/约束名
/// 判断是否已存在，<c>Seed</c> 按主键/唯一键逐个补种。因此重复执行不会产生重复数据，也不会覆盖
/// 管理员已经改过的配置项。
/// </summary>
public sealed class SystemStartupService(
    AppDbContext db,
    IConfiguration config,
    ILogger<SystemStartupService> logger)
{
    /// <summary>执行启动序列，返回本次启动后的规模快照（用于日志与健康检查）。</summary>
    public StartupReport Startup()
    {
        var tableCount = db.Model.GetEntityTypes().Count();

        DbInitializer.EnsureDatabase(db);
        DbInitializer.EnsureSchemaUpgrades(db);
        DbInitializer.Seed(db, config);

        var report = new StartupReport(
            TableCount: tableCount,
            ConfigCount: db.SystemConfigs.Count(),
            AccountCount: db.SystemUsers.Count(),
            MemberLevelCount: db.MemberRights.Count());

        logger.LogInformation(
            "startup() 完成：{Tables} 个实体映射就绪，配置项 {Configs} 条，账号 {Accounts} 个，会员等级 {Levels} 档",
            report.TableCount, report.ConfigCount, report.AccountCount, report.MemberLevelCount);

        return report;
    }
}

/// <summary>startup() 的输出：本次启动后系统应具备的规模基线。</summary>
public sealed record StartupReport(int TableCount, int ConfigCount, int AccountCount, int MemberLevelCount);

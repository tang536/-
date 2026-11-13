namespace ERental.Services;

///<summary>后台定时回收「超时未支付」的订单</summary>
public class PendingOrderCleanupService(
    IServiceScopeFactory scopeFactory, ILogger<PendingOrderCleanupService> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<PendingOrderCleanupService> _logger = logger;

    ///<summary>扫描周期</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        //启动后先等一会儿
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                //清理失败不能影响主流程：记日志后等下一轮重试
                _logger.LogWarning(ex, "超时订单清理任务执行失败，将在下一周期重试");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    ///<summary>执行一轮巡检：取锁</summary>
    private async Task SweepOnceAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();

        //多实例部署时用 MySQL 命名锁选主
        var locks = scope.ServiceProvider.GetRequiredService<DbLockService>();
        await using var handle = await locks.TryAcquireAsync("erental_pending_cleanup", TimeSpan.Zero, stoppingToken);
        if (handle is null) return;

        //拿到锁不等于「一直持有」：GET_LOCK 绑在物理连接上
        if (!await handle.IsHeldAsync())
        {
            _logger.LogWarning("命名锁 erental_pending_cleanup 在清理前自检失败（连接可能已失效），本轮跳过");
            return;
        }

        var orders = scope.ServiceProvider.GetRequiredService<OrderService>();
        //OrderService 不在本次改动范围，不能给它加 ct 参数
        var released = await orders.ReleaseExpiredPendingOrders().WaitAsync(stoppingToken);
        if (released > 0)
            _logger.LogInformation("已自动取消 {Count} 笔超时未支付订单并释放对应车辆", released);

        //写完之后再确认一次
        if (!await handle.IsHeldAsync())
        {
            _logger.LogError("命名锁 erental_pending_cleanup 在清理后自检失败：本轮可能与另一实例重叠，" + "请核对是否存在重复取消的订单（释放逻辑幂等，重复执行本身不会多退少补）");
        }
    }
}

namespace ERental.Services;

/// <summary>
/// 后台定时回收「超时未支付」的订单，避免车辆被永久锁死。
///
/// 背景：顾客点开支付宝收银台后支付失败或直接关掉页面就走了，订单会一直卡在「待支付」、
/// 车辆一直「在租」。若不主动兜底，后面的顾客永远租不到这辆车。
///
/// 与 <see cref="OrderService.ReleaseExpiredPendingOrders"/> 的分工：
/// 那个方法是「惰性回收」，在有人访问订单列表/发起支付时顺手清一次，负责实时性；
/// 本服务是「主动定时回收」，即使整站无人访问也会按周期清理，负责兜底。
/// 两者都调用同一段释放逻辑，不会重复释放（第二次查不到超时单即返回 0）。
/// </summary>
public class PendingOrderCleanupService(
    IServiceScopeFactory scopeFactory, ILogger<PendingOrderCleanupService> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<PendingOrderCleanupService> _logger = logger;

    /// <summary>扫描周期。待支付有效期为 15 分钟，每分钟扫一次足以做到"到期后最多 1 分钟内释放"。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后先等一会儿，避免和应用初始化抢数据库连接
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();

                // 多实例部署时用 MySQL 命名锁选主：没抢到锁说明另一台正在扫，本轮直接跳过。
                // 抢不到不算异常——释放逻辑本身是幂等的，加锁只是避免两台重复扫同一批订单。
                var locks = scope.ServiceProvider.GetRequiredService<DbLockService>();
                await using var handle = await locks.TryAcquireAsync("erental_pending_cleanup", TimeSpan.Zero);

                if (handle is not null)
                {
                    var orders = scope.ServiceProvider.GetRequiredService<OrderService>();
                    var released = await orders.ReleaseExpiredPendingOrders();
                    if (released > 0)
                        _logger.LogInformation("已自动取消 {Count} 笔超时未支付订单并释放对应车辆", released);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // 清理失败不能影响主流程：记日志后等下一轮重试
                _logger.LogWarning(ex, "超时订单清理任务执行失败，将在下一周期重试");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

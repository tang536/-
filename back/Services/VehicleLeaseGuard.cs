using ERental.Data;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>
/// 「一车一租」的并发闸门。
///
/// 背景（实测暴露）：下单原本是「先查状态、再写状态」的两步流程——
///   FindAsync 读到「空闲」→ 判定可租 → 另一次 SaveChanges 才把车改成「在租」。
/// 两个请求同时进来时都能读到「空闲」，于是同一辆车被租出去多次。
/// 实测 6 个并发请求对同一辆车下单，6 单全部成功，库里留下 6 张订单 + 6 条支付记录。
///
/// 为什么不用乐观锁（[ConcurrencyCheck] + version 列）：
/// 那需要给 vehicle 表加列，会破坏与设计文档 17 张表 / 134 字段的一致性
/// （与 SessionRegistry「进程内单例、不加表不加列」是同一个取舍）。
/// 这里改用**带条件的单条 UPDATE**：
///
///     UPDATE vehicle SET vehicle_status = '在租'
///      WHERE vehicle_id = @id AND vehicle_status = '空闲'
///
/// InnoDB 对这条语句加行锁，判定与写入是同一个原子操作：
/// 并发的第二个请求会被阻塞到第一个提交后再判定，此时条件已不成立（影响 0 行）。
/// 因此同一辆车**只有一次更新能成功**，不需要任何表结构改动。
///
/// 释放同理（见 <see cref="TryReleaseAsync"/>）：判定与写入必须在同一句里完成，
/// 否则「判完再写」之间仍然存在竞态窗口。
/// </summary>
public static class VehicleLeaseGuard
{
    /// <summary>仍在占用车辆的订单状态；「已完结 / 已取消」不再占用。</summary>
    private static readonly string[] OccupyingStatuses = ["待支付", "已支付", "租赁中"];

    /// <summary>
    /// 原子占用车辆。返回 false 表示这辆车在判定瞬间已不是「空闲」，
    /// 调用方必须立刻放弃本次下单，**不能继续写订单**。
    /// </summary>
    public static async Task<bool> TryClaimAsync(AppDbContext db, int vehicleId)
    {
        FormattableString sql = $@"
            UPDATE vehicle
               SET vehicle_status = {VehicleStatuses.Rented}
             WHERE vehicle_id = {vehicleId}
               AND vehicle_status = {VehicleStatuses.Idle}";
        return await db.Database.ExecuteSqlAsync(sql) > 0;
    }

    /// <summary>
    /// 释放车辆（订单取消 / 超时回收 / 归还结算共用）。
    ///
    /// 三个条件必须同时满足，全部写在同一条 UPDATE 里：
    ///  ① 车辆当前是「在租」—— 维保 / 报废的车不能被一次订单取消顺手改回「空闲」；
    ///  ② 没有**其它**订单还在占用这辆车 —— 超时回收存在天然的时间差：
    ///     订单 14:59 到期待回收，14:58 顾客刚把它重新租走，
    ///     若这里无条件置「空闲」，正在被新顾客骑走的车就会被标成可租，被第三人再租一次；
    ///  ③ 判定与写入原子完成，避免「判完再写」之间的窗口。
    /// </summary>
    /// <param name="db">数据库上下文；必须与后续 SaveChanges 是同一个实例，才能共用事务。</param>
    /// <param name="vehicleId">要释放的车辆。</param>
    /// <param name="excludeOrderId">本次要关闭的订单；它自身不算占用者。</param>
    /// <returns>true = 已释放为「空闲」；false = 仍被其它订单占用，或状态本就不是「在租」。</returns>
    public static async Task<bool> TryReleaseAsync(AppDbContext db, int vehicleId, int excludeOrderId)
    {
        FormattableString sql = $@"
            UPDATE vehicle
               SET vehicle_status = {VehicleStatuses.Idle}
             WHERE vehicle_id = {vehicleId}
               AND vehicle_status = {VehicleStatuses.Rented}
               AND NOT EXISTS (
                     SELECT 1 FROM rental_order
                      WHERE vehicle_id = {vehicleId}
                        AND order_id <> {excludeOrderId}
                        AND order_status IN ({OccupyingStatuses[0]}, {OccupyingStatuses[1]}, {OccupyingStatuses[2]})
                   )";
        return await db.Database.ExecuteSqlAsync(sql) > 0;
    }
}

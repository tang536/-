using ERental.Data;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>
/// 车辆状态迁移的并发闸门（维保 / 报废），与 <see cref="VehicleLeaseGuard"/> 是同一套思路：
/// 把「先查状态、再写状态」的两步流程压成**一条带条件的 UPDATE**，
/// 由 InnoDB 行锁保证判定与写入原子完成，从而不需要给 vehicle 表加 version 列。
///
/// 为什么需要它（与租赁同源的竞态）：
///  ① 送修：管理员 A 看到「空闲」准备送修，同一瞬间顾客 B 把它租走。
///     原实现先 FindAsync 判「不是在租」，再 SaveChanges 写「维保」——
///     两次判定之间车已被租走，最终库里出现「在租 + 维保工单」的矛盾数据。
///  ② 报废：同一窗口下，车可以被既标「报废」又挂上活跃订单，等于报废车还在收租金。
///  ③ 修完放行：完成一张工单就把车置「空闲」，若这辆车还挂着**另一张未完成工单**，
///     车会一边「空闲可租」一边「还在维修」。
///
/// 这里同样不引入表结构变更，保住与设计文档 17 张表 / 134 字段的一致性。
/// </summary>
public static class VehicleStateGuard
{
    /// <summary>
    /// 原子转入「维保」。允许的起点只有「空闲」与「维保」：
    /// 「在租」的车不能送修（先归还结算），「报废」的车不能建工单。
    ///
    /// 起点包含「维保」是刻意保留的既有语义——同一辆车可以先后开多张工单
    /// （先修电机、再补胎），第二张工单不该因为车已在维保状态而被拒。
    /// </summary>
    /// <param name="db">数据库上下文；须与后续 SaveChanges 为同一实例，才能共用一个事务。</param>
    /// <param name="vehicleId">要送修的车辆。</param>
    /// <returns>true = 已转入「维保」；false = 该车此刻正在租赁中、已报废或不存在。</returns>
    public static async Task<bool> TryEnterMaintenanceAsync(AppDbContext db, int vehicleId)
    {
        FormattableString sql = $@"
            UPDATE vehicle
               SET vehicle_status = {VehicleStatuses.Maintenance}
             WHERE vehicle_id = {vehicleId}
               AND vehicle_status IN ({VehicleStatuses.Idle}, {VehicleStatuses.Maintenance})";
        return await db.Database.ExecuteSqlAsync(sql) > 0;
    }

    /// <summary>
    /// 维保完成后的放行：把车从「维保」送回「空闲」。
    ///
    /// 三个条件写在同一条 UPDATE 里：
    ///  ① 当前必须是「维保」—— 已报废的车不能被一次完工顺手改回可租；
    ///  ② 这辆车没有**其它**未完成工单 —— 一车多工单时，只有最后一张完工才真正放行；
    ///     否则「先修好的那张」会把还在维修的车标成可租，被第三人租走；
    ///  ③ 判定与写入原子完成。
    ///
    /// 调用方必须**先把当前工单落成「已完成」再调用**，否则它会把自己算进未完成工单里。
    /// </summary>
    /// <param name="db">数据库上下文。</param>
    /// <param name="vehicleId">工单对应的车辆。</param>
    /// <param name="workOrderId">刚完工的工单；它自身不计入「未完成工单」。</param>
    /// <returns>true = 已放行回「空闲」；false = 仍有未完成工单，或车辆不在「维保」状态。</returns>
    public static async Task<bool> TryReleaseAfterMaintenanceAsync(AppDbContext db, int vehicleId, int workOrderId)
    {
        FormattableString sql = $@"
            UPDATE vehicle
               SET vehicle_status = {VehicleStatuses.Idle}
             WHERE vehicle_id = {vehicleId}
               AND vehicle_status = {VehicleStatuses.Maintenance}
               AND NOT EXISTS (
                     SELECT 1 FROM maintenance_order
                      WHERE vehicle_id = {vehicleId}
                        AND work_order_id <> {workOrderId}
                        AND status <> '已完成'
                   )";
        return await db.Database.ExecuteSqlAsync(sql) > 0;
    }

    /// <summary>
    /// 原子转入「报废」。允许的起点是「空闲」与「维保」——
    /// 修不好的车可以直接报废，但「在租」的车必须先结算归还，
    /// 否则会出现「报废车仍在计费」的账实不符。
    /// </summary>
    /// <param name="db">数据库上下文；须与后续 SaveChanges 为同一实例，才能共用一个事务。</param>
    /// <param name="vehicleId">要报废的车辆。</param>
    /// <returns>true = 已转入「报废」；false = 该车正在租赁中、已报废或不存在。</returns>
    public static async Task<bool> TryScrapAsync(AppDbContext db, int vehicleId)
    {
        FormattableString sql = $@"
            UPDATE vehicle
               SET vehicle_status = {VehicleStatuses.Scrapped}
             WHERE vehicle_id = {vehicleId}
               AND vehicle_status IN ({VehicleStatuses.Idle}, {VehicleStatuses.Maintenance})";
        return await db.Database.ExecuteSqlAsync(sql) > 0;
    }

    /// <summary>
    /// 管理员手动把车置回「空闲」（车辆管理的状态切换接口用）。
    ///
    /// 与 <see cref="TryReleaseAfterMaintenanceAsync"/> 的区别：这里**不检查**未完成工单。
    /// 因为这是人工显式操作——工单丢失、误开单等情况需要人工兜底恢复；
    /// 但「在租」仍被排除在外（租赁中的车只能通过结算释放，否则租金会算错），
    /// 「报废」也不允许恢复（需求 4.2.2 规定报废不可逆）。
    /// </summary>
    /// <param name="db">数据库上下文。</param>
    /// <param name="vehicleId">要置空闲的车辆。</param>
    /// <returns>true = 已置为「空闲」；false = 该车正在租赁中或已报废。</returns>
    public static async Task<bool> TryForceIdleAsync(AppDbContext db, int vehicleId)
    {
        FormattableString sql = $@"
            UPDATE vehicle
               SET vehicle_status = {VehicleStatuses.Idle}
             WHERE vehicle_id = {vehicleId}
               AND vehicle_status IN ({VehicleStatuses.Idle}, {VehicleStatuses.Maintenance})";
        return await db.Database.ExecuteSqlAsync(sql) > 0;
    }
}

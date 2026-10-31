using ERental.Data;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>车辆状态迁移的并发闸门（维保 / 报废），与 是同一套思路： 把「先查状态、再写状态」的两步流程压成一条带条件的 U…</summary>
public static class VehicleStateGuard
{
    /// <summary>原子转入「维保」</summary>
    public static async Task<bool> TryEnterMaintenanceAsync(AppDbContext db, int vehicleId)
    {
        FormattableString sql = $@"
            UPDATE vehicle
               SET vehicle_status = {VehicleStatuses.Maintenance}
             WHERE vehicle_id = {vehicleId}
               AND vehicle_status IN ({VehicleStatuses.Idle}, {VehicleStatuses.Maintenance})";
        return await db.Database.ExecuteSqlAsync(sql) > 0;
    }

    /// <summary>维保完成后的放行：把车从「维保」送回「空闲」</summary>
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

    /// <summary>原子转入「报废」</summary>
    public static async Task<bool> TryScrapAsync(AppDbContext db, int vehicleId)
    {
        FormattableString sql = $@"
            UPDATE vehicle
               SET vehicle_status = {VehicleStatuses.Scrapped}
             WHERE vehicle_id = {vehicleId}
               AND vehicle_status IN ({VehicleStatuses.Idle}, {VehicleStatuses.Maintenance})";
        return await db.Database.ExecuteSqlAsync(sql) > 0;
    }

    /// <summary>管理员手动把车置回「空闲」（车辆管理的状态切换接口用）</summary>
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

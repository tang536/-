using ERental.Data;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>「一车一租」的并发闸门</summary>
public static class VehicleLeaseGuard
{
    /// <summary>仍在占用车辆的订单状态</summary>
    private static readonly string[] OccupyingStatuses = ["待支付", "已支付", "租赁中"];

    /// <summary>原子占用车辆</summary>
    public static async Task<bool> TryClaimAsync(AppDbContext db, int vehicleId)
    {
        FormattableString sql = $@"
            UPDATE vehicle
               SET vehicle_status = {VehicleStatuses.Rented}
             WHERE vehicle_id = {vehicleId}
               AND vehicle_status = {VehicleStatuses.Idle}";
        return await db.Database.ExecuteSqlAsync(sql) > 0;
    }

    /// <summary>释放车辆（订单取消 / 超时回收 / 归还结算共用）</summary>
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

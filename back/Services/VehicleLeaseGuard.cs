using ERental.Data;

namespace ERental.Services;

///<summary>「一车一租」的并发闸门：占用/释放判定全部由数据库存储过程原子完成</summary>
internal static class VehicleLeaseGuard
{
    ///<summary>原子占用车辆：只有「空闲」车辆能被占用，返回 false 表示已被他人抢先</summary>
    public static Task<bool> TryClaimAsync(AppDbContext db, int vehicleId)
        => StoredProcedures.ClaimVehicleAsync(db, vehicleId);

    ///<summary>释放车辆（订单取消/超时回收/归还结算共用）：本单之外没有活跃订单占着才置空闲</summary>
    public static Task<bool> TryReleaseAsync(AppDbContext db, int vehicleId, int excludeOrderId)
        => StoredProcedures.ReleaseVehicleAsync(db, vehicleId, excludeOrderId);
}

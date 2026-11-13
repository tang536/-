using System.Data;
using System.Data.Common;
using ERental.Data;
using ERental.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERental.Services;

/// <summary>
/// 存储过程调用入口。
/// 统一走 CALL + ExecuteReader 读取结果集，不使用 ParameterDirection.Output：
/// Oracle MySql.Data 对存储过程 OUT 参数的返回时机与类型转换存在差异，
/// 而结果集返回对两种驱动（mysqlconnector / MySql.Data）语义一致。
/// 命令会挂到 EF 当前事务上：占车/释放车辆这类单条原子 UPDATE 可被外层事务包裹。
/// </summary>
internal static class StoredProcedures
{
    /// <summary>原子占车：只有「空闲」车辆能被占用</summary>
    public static async Task<bool> ClaimVehicleAsync(AppDbContext db, int vehicleId, CancellationToken ct = default)
    {
        var rows = await QueryAsync(db, "sp_claim_vehicle", [vehicleId], ct);
        return ReadInt(rows, "claimed") > 0;
    }

    /// <summary>原子释放车辆：本单之外没有活跃订单占着这辆车时才置空闲</summary>
    public static async Task<bool> ReleaseVehicleAsync(AppDbContext db, int vehicleId, int excludeOrderId, CancellationToken ct = default)
    {
        var rows = await QueryAsync(db, "sp_release_vehicle", [vehicleId, excludeOrderId], ct);
        return ReadInt(rows, "released") > 0;
    }

    /// <summary>工作人员取消订单：锁单、校验、作废待支付流水、释放车辆均由过程内事务完成</summary>
    public static async Task CancelOrderAsync(AppDbContext db, int orderId, string reason, CancellationToken ct = default)
        => await QueryAsync(db, "sp_cancel_order", [orderId, reason], ct);

    /// <summary>顾客自助取消待支付订单：过程内额外校验订单归属</summary>
    public static async Task CancelPendingOrderAsync(AppDbContext db, int orderId, int customerId, CancellationToken ct = default)
        => await QueryAsync(db, "sp_cancel_pending_order", [orderId, customerId], ct);

    /// <summary>续租：按天计费、写续租记录并同步订单到期时间</summary>
    public static async Task<RenewalDto> ApplyRenewalAsync(AppDbContext db, int orderId, int days, int? operatorId, CancellationToken ct = default)
    {
        var rows = await QueryAsync(db, "sp_apply_renewal", [orderId, days, operatorId], ct);
        var row = rows.FirstOrDefault() ?? throw new BusinessException("续租失败，请稍后重试");
        return new RenewalDto
        {
            RenewalId = ReadInt(row, "renewal_id"),
            OrderId = ReadInt(row, "order_id"),
            RenewalDays = ReadInt(row, "renewal_days"),
            RenewalFee = ReadDecimal(row, "renewal_fee"),
            OldEndTime = ReadDateTime(row, "old_end_time"),
            NewEndTime = ReadDateTime(row, "new_end_time"),
            RenewalTime = ReadDateTime(row, "renewal_time"),
        };
    }

    /// <summary>批量回收超时待支付订单，返回本批取消数量</summary>
    public static async Task<int> CancelExpiredPendingOrdersAsync(
        AppDbContext db, int timeoutMinutes, int graceMinutes, int batch, CancellationToken ct = default)
    {
        var rows = await QueryAsync(db, "sp_cancel_expired_pending_orders", [timeoutMinutes, graceMinutes, batch], ct);
        return ReadInt(rows, "cancelled");
    }

    /// <summary>执行 CALL，并把每个结果集读成「列名 → 值」的字典</summary>
    private static async Task<List<Dictionary<string, object?>>> QueryAsync(
        AppDbContext db, string procedure, object?[] args, CancellationToken ct)
    {
        await using var cmd = await CreateCommandAsync(db, procedure, args, ct);
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        do
        {
            if (reader.FieldCount <= 0) continue;
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            //过程只声明一个结果集；读到有字段的结果集即可停止
            break;
        } while (await reader.NextResultAsync(ct));
        return rows;
    }

    private static async Task<DbCommand> CreateCommandAsync(AppDbContext db, string procedure, object?[] args, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);

        var placeholders = string.Join(",", args.Select((_, i) => $"@p{i}"));
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"CALL `{procedure}`({placeholders})";
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        for (var i = 0; i < args.Length; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = $"@p{i}";
            p.Value = args[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private static int ReadInt(List<Dictionary<string, object?>> rows, string column) =>
        rows.Count == 0 ? 0 : ReadInt(rows[0], column);

    private static int ReadInt(Dictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var v) && v is not null ? Convert.ToInt32(v) : 0;

    private static decimal ReadDecimal(Dictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var v) && v is not null ? Convert.ToDecimal(v) : 0m;

    private static DateTime ReadDateTime(Dictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var v) && v is not null ? Convert.ToDateTime(v) : DateTime.Now;
}

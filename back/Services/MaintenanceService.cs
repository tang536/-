using System.Data;
using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class MaintenanceService(AppDbContext db, ImageStorage images)
{
    private readonly AppDbContext _db = db;
    private readonly ImageStorage _images = images;

    public async Task<string> SaveImage(IFormFile? file) => await _images.SaveAsync(file, "maintenance");

    /// <summary>工单列表：投影为 DTO，避免 Vehicle 导航属性被关系修复回填后形成循环引用</summary>
    public async Task<List<MaintenanceOrderDto>> GetAll(string? status = null)
    {
        var q = _db.MaintenanceOrders.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(status)) q = q.Where(m => m.Status == status);

        return await q
            .OrderByDescending(m => m.CreateTime)
            .Select(m => new MaintenanceOrderDto
            {
                WorkOrderId = m.WorkOrderId,
                WorkOrderNo = m.WorkOrderNo,
                VehicleId = m.VehicleId,
                VehicleNo = m.Vehicle != null ? m.Vehicle.VehicleNo : "",
                Brand = m.Vehicle != null ? m.Vehicle.Brand : null,
                FaultType = m.FaultType,
                FaultDescription = m.FaultDescription,
                MaintainFee = m.MaintainFee,
                Status = m.Status,
                CreateTime = m.CreateTime,
                FinishTime = m.FinishTime,
                OperatorId = m.OperatorId,
                FaultImage = m.FaultImage
            })
            .ToListAsync();
    }

    public async Task<MaintenanceOrderDto> Create(CreateMaintenanceRequest req, int? operatorId)
    {
        // 用 AsNoTracking 读车辆：状态改由下面的原子 UPDATE 完成，
        // 若把它读进跟踪器，SaveChanges 会把内存里的旧状态一起写回，抵消并发保护。
        var v = await _db.Vehicles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.VehicleId == req.VehicleId)
            ?? throw new Exception("车辆不存在，请重新选择");

        // 快速失败：明显不可送修的车尽早返回，不必走后面的写操作。
        // ⚠️ 这不是并发防线——两个请求可以同时通过这两句，真正的判定在汽车状态闸门里。
        if (v.VehicleStatus == "报废") throw new Exception($"车辆 {v.VehicleNo} 已报废，不能创建维保工单");
        if (v.VehicleStatus == "在租") throw new Exception($"车辆 {v.VehicleNo} 正在租赁中，请先完成归还结算再送修");

        // 从「占用/报废记录」开始一次性提交：状态迁移失败时工单必须一起消失，
        // 否则会留下「车没进维保、工单却已建」的幽灵工单。
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);

            // 原子转入「维保」：判定与写入同一条 UPDATE。
            // 原实现先 FindAsync 判「不是在租」再 SaveChanges 写「维保」，
            // 两次之间顾客可以把车租走，最终留下「在租 + 维保工单」的矛盾数据。
            if (!await VehicleStateGuard.TryEnterMaintenanceAsync(_db, req.VehicleId))
            {
                // 失败必然是在刚才那一瞬间状态变了：重读最新状态，给出准确原因而不是笼统提示
                var latest = await _db.Vehicles.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.VehicleId == req.VehicleId);
                var why = latest == null ? "已被删除"
                    : latest.VehicleStatus == "在租" ? "刚被顾客租走，请先完成归还结算"
                    : latest.VehicleStatus == "报废" ? "刚被报废"
                    : "状态已变更";
                throw new Exception($"车辆 {v.VehicleNo} {why}，请重新选择");
            }

            // 现场照片随工单一次性落库，用于锁定维保位置；工单没有修改接口，故照片不可被篡改
            var m = MaintenanceOrder.Report(
                await NextWorkOrderNoAsync(), req.VehicleId, req.FaultType,
                req.FaultDescription, req.FaultImage, operatorId);

            _db.MaintenanceOrders.Add(m);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            return new MaintenanceOrderDto
            {
                WorkOrderId = m.WorkOrderId,
                WorkOrderNo = m.WorkOrderNo,
                VehicleId = m.VehicleId,
                VehicleNo = v.VehicleNo,
                Brand = v.Brand,
                FaultType = m.FaultType,
                FaultDescription = m.FaultDescription,
                MaintainFee = m.MaintainFee,
                Status = m.Status,
                CreateTime = m.CreateTime,
                OperatorId = m.OperatorId,
                FaultImage = m.FaultImage
            };
        }
        catch
        {
            // 回滚后车辆状态自动退回原值，不会停在「维保」而工单不存在
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task Complete(int id, decimal fee)
    {
        // 只读一次车辆归属，状态判定交给下面的条件 UPDATE
        var m = await _db.MaintenanceOrders.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.WorkOrderId == id)
                ?? throw new Exception("工单不存在");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);

            // 判定与写入压成一条 UPDATE：两个前台同时点「完工」，只有第一个影响 1 行，
            // 第二个拿到 0 行 → 明确拒绝，不会重复计维修费、也不会把「完工时间」改成第二次点击的时刻。
            var now = DateTime.Now;
            var changed = await _db.Database.ExecuteSqlAsync($@"
                UPDATE maintenance_order
                   SET status = {"已完成"}, maintain_fee = {fee}, finish_time = {now}
                 WHERE work_order_id = {id} AND status <> {"已完成"}");
            if (changed == 0) throw new Exception("该工单已完成，无需重复操作");

            // 先落「已完成」再放行：放行判定要读工单状态，顺序颠倒会把自己算成未完成工单。
            // 只在「这辆车没有其它未完成工单」时才置「空闲」，
            // 否则先修好的那张工单会把还在维修的车标成可租（详见 VehicleStateGuard）。
            await VehicleStateGuard.TryReleaseAfterMaintenanceAsync(_db, m.VehicleId, id);

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>编辑维保工单内容（故障类型 / 描述 / 现场照片）</summary>
    public async Task Update(int id, UpdateMaintenanceRequest req)
    {
        var m = await _db.MaintenanceOrders.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.WorkOrderId == id)
                ?? throw new Exception("工单不存在");
        if (m.Status == "已完成") throw new Exception("已完成的工单不可编辑");

        var faultDesc = (req.FaultDescription ?? "").Trim();
        if (string.IsNullOrWhiteSpace(faultDesc)) throw new Exception("故障描述不能为空");
        var faultType = string.IsNullOrWhiteSpace(req.FaultType) ? "其他" : req.FaultType.Trim();
        var faultImage = string.IsNullOrWhiteSpace(req.FaultImage) ? null : req.FaultImage.Trim();

        var changed = await _db.Database.ExecuteSqlAsync($@"
            UPDATE maintenance_order
               SET fault_type = {faultType},
                   fault_description = {faultDesc},
                   fault_image = {faultImage}
             WHERE work_order_id = {id} AND status <> '已完成'");
        if (changed == 0) throw new Exception("该工单已不可编辑");
    }

    /// <summary>撤回维保工单：把工单置为「已撤回」终态，并在「该车辆没有其它未完成工单」时把车从「维保」释放回「空闲」</summary>
    public async Task Withdraw(int id)
    {
        var m = await _db.MaintenanceOrders.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.WorkOrderId == id)
                ?? throw new Exception("工单不存在");
        if (m.Status == "已完成") throw new Exception("已完成的工单不可撤回");
        if (m.Status == "已撤回") throw new Exception("该工单已撤回");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);

            var changed = await _db.Database.ExecuteSqlAsync($@"
                UPDATE maintenance_order
                   SET status = {"已撤回"}
                 WHERE work_order_id = {id} AND status NOT IN ('已完成', '已撤回')");
            if (changed == 0) throw new Exception("该工单已不可撤回");

            // 只在「没有其它未完成工单」时才把车置回空闲；否则车辆仍在维保，交由其它工单决定。
            await VehicleStateGuard.TryReleaseAfterMaintenanceAsync(_db, m.VehicleId, id);

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<List<ScrapRecordDto>> GetScraps() =>
        await _db.ScrapRecords
            .AsNoTracking()
            .OrderByDescending(s => s.ScrapTime)
            .Select(s => new ScrapRecordDto
            {
                ScrapId = s.ScrapId,
                VehicleId = s.VehicleId,
                VehicleNo = s.Vehicle != null ? s.Vehicle.VehicleNo : "",
                Brand = s.Vehicle != null ? s.Vehicle.Brand : null,
                ScrapReason = s.ScrapReason,
                ScrapTime = s.ScrapTime,
                OperatorId = s.OperatorId
            })
            .ToListAsync();

    public async Task ScrapVehicle(int vehicleId, string reason, int operatorId)
    {
        // 只读：用于错误文案；状态判定交给下面的原子 UPDATE
        var v = await _db.Vehicles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.VehicleId == vehicleId) ?? throw new Exception("车辆不存在");
        if (v.VehicleStatus == "报废") throw new Exception($"车辆 {v.VehicleNo} 已处于报废状态");
        if (v.VehicleStatus == "在租") throw new Exception($"车辆 {v.VehicleNo} 正在租赁中，请先完成归还结算");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);

            // 原子转入「报废」：原实现先判「不是在租」再写状态，
            // 那个窗口里车可能刚被顾客租走，结果就是「报废车仍在计费」。
            if (!await VehicleStateGuard.TryScrapAsync(_db, vehicleId))
            {
                var latest = await _db.Vehicles.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.VehicleId == vehicleId);
                var why = latest == null ? "已被删除"
                    : latest.VehicleStatus == "在租" ? "刚被顾客租走，请先完成归还结算"
                    : latest.VehicleStatus == "报废" ? "已处于报废状态"
                    : "状态已变更";
                throw new Exception($"车辆 {v.VehicleNo} {why}，请刷新后重试");
            }

            _db.ScrapRecords.Add(new ScrapRecord
            {
                VehicleId = vehicleId,
                ScrapReason = reason,
                OperatorId = operatorId,
                ScrapTime = DateTime.Now
            });
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>生成工单号：WB + 毫秒级时间戳，冲突时重试</summary>
    private async Task<string> NextWorkOrderNoAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            var no = "WB" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
            if (!await _db.MaintenanceOrders.AnyAsync(x => x.WorkOrderNo == no)) return no;
            await Task.Delay(2);
        }
        return "WB" + Guid.NewGuid().ToString("N")[..17];
    }
}

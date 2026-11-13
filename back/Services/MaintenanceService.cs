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

    ///<summary>工单列表</summary>
    public async Task<List<MaintenanceOrderDto>> GetAll(string? status = null)
    {
        var q = _db.MaintenanceOrders.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(status)) q = q.Where(m => m.Status == status);

        return await q.OrderByDescending(m => m.CreateTime).Select(m => new MaintenanceOrderDto
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
        //用 AsNoTracking 读车辆
        var v = await _db.Vehicles.AsNoTracking().FirstOrDefaultAsync(x => x.VehicleId == req.VehicleId) ?? throw new BusinessException("车辆不存在，请重新选择");

        //快速失败：明显不可送修的车尽早返回，不必走后面的写操作
        if (v.VehicleStatus == "报废") throw new BusinessException($"车辆 {v.VehicleNo} 已报废，不能创建维保工单");
        if (v.VehicleStatus == "在租") throw new BusinessException($"车辆 {v.VehicleNo} 正在租赁中，请先完成归还结算再送修");

        //从「占用/报废记录」开始一次性提交
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);

            //原子转入「维保」：判定与写入同一条 UPDATE
            if (!await VehicleStateGuard.TryEnterMaintenanceAsync(_db, req.VehicleId))
            {
                //失败必然是在刚才那一瞬间状态变了：重读最新状态，给出准确原因而不是笼统提示
                var latest = await _db.Vehicles.AsNoTracking().FirstOrDefaultAsync(x => x.VehicleId == req.VehicleId);
                var why = latest == null ? "已被删除" : latest.VehicleStatus switch
                {
                    "在租" => "刚被顾客租走，请先完成归还结算",
                    "报废" => "刚被报废",
                    _ => "状态已变更"
                };
                throw new BusinessException($"车辆 {v.VehicleNo} {why}，请重新选择");
            }

            //现场照片随工单一次性落库，用于锁定维保位置
            var m = MaintenanceOrder.Report(await NextWorkOrderNoAsync(), req.VehicleId, req.FaultType, req.FaultDescription, req.FaultImage, operatorId);

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
            //回滚后车辆状态自动退回原值，不会停在「维保」而工单不存在
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task Complete(int id, decimal fee)
    {
        if (fee < 0) throw new BusinessException("维修费不能为负数");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);

            //先锁行、再改状态：两个前台同时点「完工」时，第二个会等锁，拿到锁后读到的是「已完成」
            var m = await DbRowLock.LockWorkOrderAsync(_db, id) ?? throw new BusinessException("工单不存在");
            m.Complete(fee, DateTime.Now);
            await _db.SaveChangesAsync();

            //先落「已完成」再放行：放行判定要读工单状态，顺序颠倒会把自己算成未完成工单
            await VehicleStateGuard.TryReleaseAfterMaintenanceAsync(_db, m.VehicleId, id);

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    ///<summary>编辑维保工单内容（故障类型/描述/现场照片）</summary>
    public async Task Update(int id, UpdateMaintenanceRequest req)
    {
        var m = await _db.MaintenanceOrders.AsNoTracking().FirstOrDefaultAsync(x => x.WorkOrderId == id) ?? throw new BusinessException("工单不存在");
        if (m.Status == "已完成") throw new BusinessException("已完成的工单不可编辑");

        var faultDesc = (req.FaultDescription ?? "").Trim();
        if (string.IsNullOrWhiteSpace(faultDesc)) throw new BusinessException("故障描述不能为空");
        var faultType = string.IsNullOrWhiteSpace(req.FaultType) ? "其他" : req.FaultType.Trim();
        var faultImage = string.IsNullOrWhiteSpace(req.FaultImage) ? null : req.FaultImage.Trim();

        var changed = await _db.Database.ExecuteSqlAsync($@"
            UPDATE maintenance_order
               SET fault_type = {faultType},
                   fault_description = {faultDesc},
                   fault_image = {faultImage}
             WHERE work_order_id = {id} AND status <> '已完成'");
        if (changed == 0) throw new BusinessException("该工单已不可编辑");
    }

    ///<summary>撤回维保工单：把工单置为「已撤回」终态</summary>
    public async Task Withdraw(int id)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);

            //同 Complete
            var m = await DbRowLock.LockWorkOrderAsync(_db, id) ?? throw new BusinessException("工单不存在");
            m.Withdraw();
            await _db.SaveChangesAsync();

            //只在「没有其它未完成工单」时才把车置回空闲
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
        await _db.ScrapRecords.AsNoTracking().OrderByDescending(s => s.ScrapTime).Select(s => new ScrapRecordDto
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
        //只读：用于错误文案
        var v = await _db.Vehicles.AsNoTracking().FirstOrDefaultAsync(x => x.VehicleId == vehicleId) ?? throw new BusinessException("车辆不存在");
        if (v.VehicleStatus == "报废") throw new BusinessException($"车辆 {v.VehicleNo} 已处于报废状态");
        if (v.VehicleStatus == "在租") throw new BusinessException($"车辆 {v.VehicleNo} 正在租赁中，请先完成归还结算");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await DbRowLock.PrepareAsync(_db);

            //原子转入「报废」：原实现先判「不是在租」再写状态，那个窗口里车可能刚被顾客租走
            if (!await VehicleStateGuard.TryScrapAsync(_db, vehicleId))
            {
                var latest = await _db.Vehicles.AsNoTracking().FirstOrDefaultAsync(x => x.VehicleId == vehicleId);
                var why = latest == null ? "已被删除" : latest.VehicleStatus switch
                {
                    "在租" => "刚被顾客租走，请先完成归还结算",
                    "报废" => "已处于报废状态",
                    _ => "状态已变更"
                };
                throw new BusinessException($"车辆 {v.VehicleNo} {why}，请刷新后重试");
            }

            _db.ScrapRecords.Add(ScrapRecord.Create(vehicleId, reason, operatorId, DateTime.Now));
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    ///<summary>生成工单号：WB+毫秒级时间戳，冲突时重试</summary>
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

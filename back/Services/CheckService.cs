using ERental.Data;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class CheckService(AppDbContext db)
{
    private readonly AppDbContext _db = db;

    public async Task<List<CarCheckList>> GetAll(DateTime? date = null)
    {
        var q = _db.CarCheckLists.Include(c => c.Vehicle).AsQueryable();
        if (date.HasValue) q = q.Where(c => c.CheckDate.Date == date.Value.Date);
        return await q.OrderByDescending(c => c.CheckDate).ToListAsync();
    }

    public async Task<CarCheckList> AddCheck(int vehicleId, string actualStatus, string? diffDesc, int? operatorId = null)
    {
        var vehicle = await _db.Vehicles.FindAsync(vehicleId)
            ?? throw new Exception("车辆不存在");
        // 盘点结果是「账实差异」的判定依据，实际状态必须落在合法取值集合内，
        // 否则差异比对（IsMatch）与盘点报表都会失去意义（需求 4.2.2 车辆盘点 / 5.1 状态数据精度）。
        if (!VehicleStatuses.IsValid(actualStatus))
            throw new Exception(VehicleStatuses.InvalidMessage("实际状态"));
        if ((diffDesc?.Length ?? 0) > 200)
            throw new Exception("差异说明不能超过 200 个字符");
        var record = new CarCheckList
        {
            CheckNo = "PD" + DateTime.Now.ToString("yyyyMMddHHmmss") + new Random().Next(100, 999),
            CheckDate = DateTime.Now,
            VehicleId = vehicleId,
            SystemStatus = vehicle.VehicleStatus,
            ActualStatus = actualStatus,
            IsMatch = vehicle.VehicleStatus == actualStatus,
            DifferenceDesc = diffDesc,
            OperatorId = operatorId
        };
        _db.CarCheckLists.Add(record);
        await _db.SaveChangesAsync();
        return record;
    }

    public async Task<CheckReport> GenerateReport(DateTime date, int? operatorId = null)
    {
        var items = await _db.CarCheckLists
            .Where(c => c.CheckDate.Date == date.Date).ToListAsync();
        var report = new CheckReport
        {
            ReportNo = "RPT" + date.ToString("yyyyMMdd") + new Random().Next(100, 999),
            CheckDate = date,
            TotalCount = items.Count,
            MatchCount = items.Count(c => c.IsMatch),
            DiffCount = items.Count(c => !c.IsMatch),
            ReportContent = $"盘点日期：{date:yyyy-MM-dd}，共{items.Count}辆车，一致{items.Count(c => c.IsMatch)}辆，差异{items.Count(c => !c.IsMatch)}辆",
            GenerateTime = DateTime.Now,
            OperatorId = operatorId
        };
        _db.CheckReports.Add(report);
        await _db.SaveChangesAsync();
        return report;
    }

    public async Task<List<CheckReport>> GetReports() =>
        await _db.CheckReports.OrderByDescending(r => r.GenerateTime).ToListAsync();
}

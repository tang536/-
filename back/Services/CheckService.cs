using ERental.Data;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class CheckService(AppDbContext db)
{
    private readonly AppDbContext _db=db;

    public async Task<List<CarCheckList>> GetAll(DateTime? date=null)
    {
        var q=_db.CarCheckLists.Include(c=>c.Vehicle).Include(c=>c.Operator).AsQueryable();
        if(date.HasValue) q=q.Where(c=>c.CheckDate.Date==date.Value.Date);
        return await q.OrderByDescending(c=>c.CheckDate).ToListAsync();
    }

    public async Task<CarCheckList> AddCheck(int vehicleId,string actualStatus,string? diffDesc,int? operatorId=null)
    {
        var vehicle=await _db.Vehicles.FindAsync(vehicleId)??throw new BusinessException("车辆不存在");
        //盘点结果是「账实差异」的判定依据，实际状态必须落在合法取值集合内
        if(!VehicleStatuses.IsValid(actualStatus)) throw new BusinessException(VehicleStatuses.InvalidMessage("实际状态"));
        if((diffDesc?.Length??0) > 200) throw new BusinessException("差异说明不能超过 200 个字符");
        var record=CarCheckList.Create(
            BusinessNumber.New("PD"),
            DateTime.Now,vehicleId,vehicle.VehicleStatus,actualStatus,diffDesc,operatorId);
        _db.CarCheckLists.Add(record);
        await _db.SaveChangesAsync();
        return record;
    }

    public async Task<CheckReport> GenerateReport(DateTime date,int? operatorId=null)
    {
        var items=await _db.CarCheckLists.Where(c=>c.CheckDate.Date==date.Date).ToListAsync();
        var matchCount=items.Count(c=>c.IsMatch);
        var diffCount=items.Count(c=>!c.IsMatch);
        var report=CheckReport.Create(
            (BusinessNumber.New("RPT",stampFormat: "yyyyMMdd",stamp: date),date),
            (items.Count,matchCount,diffCount),
            ($"盘点日期：{date:yyyy-MM-dd}，共{items.Count}辆车，一致{matchCount}辆，差异{diffCount}辆",DateTime.Now,operatorId));
        _db.CheckReports.Add(report);
        await _db.SaveChangesAsync();

        //明细归入本报表
        if(items.Count>0)
        {
            foreach(var item in items) item.AttachToReport(report.ReportId);
            await _db.SaveChangesAsync();
        }
        return report;
    }

    public async Task<List<CheckReport>> GetReports() =>
        await _db.CheckReports
            .Include(r=>r.Operator)
            .Include(r=>r.Details)
            .OrderByDescending(r=>r.GenerateTime)
            .ToListAsync();
}

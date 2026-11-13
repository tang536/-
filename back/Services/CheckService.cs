using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class CheckService(AppDbContext db)
{
    private readonly AppDbContext _db = db;

    ///<summary>盘点明细列表：车牌由联表投影带出，不下发车辆/操作员整实体</summary>
    public async Task<List<CarCheckListDto>> GetAll(DateTime? date = null)
    {
        var q = _db.CarCheckLists.AsNoTracking();
        if (date.HasValue) q = q.Where(c => c.CheckDate.Date == date.Value.Date);

        return await q.OrderByDescending(c => c.CheckDate)
            .Select(c => new CarCheckListDto
            {
                CheckId = c.CheckId,
                CheckNo = c.CheckNo,
                CheckDate = c.CheckDate,
                VehicleId = c.VehicleId,
                VehicleNo = c.Vehicle!.VehicleNo,
                SystemStatus = c.SystemStatus,
                ActualStatus = c.ActualStatus,
                IsMatch = c.IsMatch,
                DifferenceDesc = c.DifferenceDesc,
                OperatorId = c.OperatorId,
            })
            .ToListAsync();
    }

    ///<summary>录入一条盘点明细：系统状态取车辆档案当前值，实际状态由盘点人现场核对</summary>
    public async Task<CarCheckListDto> AddCheck(int vehicleId, string actualStatus, string? diffDesc, int? operatorId = null)
    {
        var vehicle = await _db.Vehicles.FindAsync(vehicleId) ?? throw new BusinessException("车辆不存在");
        //盘点结果是「账实差异」的判定依据，实际状态必须落在合法取值集合内
        if (!VehicleStatuses.IsValid(actualStatus)) throw new BusinessException(VehicleStatuses.InvalidMessage("实际状态"));
        if ((diffDesc?.Length ?? 0) > 200) throw new BusinessException("差异说明不能超过 200 个字符");
        var record = CarCheckList.Create(
            BusinessNumber.New("PD"),
            DateTime.Now, vehicleId, vehicle.VehicleStatus, actualStatus, diffDesc, operatorId);
        _db.CarCheckLists.Add(record);
        await _db.SaveChangesAsync();

        return new CarCheckListDto
        {
            CheckId = record.CheckId,
            CheckNo = record.CheckNo,
            CheckDate = record.CheckDate,
            VehicleId = record.VehicleId,
            VehicleNo = vehicle.VehicleNo,
            SystemStatus = record.SystemStatus,
            ActualStatus = record.ActualStatus,
            IsMatch = record.IsMatch,
            DifferenceDesc = record.DifferenceDesc,
            OperatorId = record.OperatorId,
        };
    }

    ///<summary>生成盘点报告：统计用 COUNT，明细归属用一条 UPDATE 回填，不加载任何明细实体</summary>
    public async Task<CheckReportDto> GenerateReport(DateTime date, int? operatorId = null)
    {
        var day = date.Date;
        //报表与明细归属必须同进同退：只写了报表却没回填 report_id，之后按报表查明细会全空
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var q = _db.CarCheckLists.Where(c => c.CheckDate.Date == day);
            var totalCount = await q.CountAsync();
            var matchCount = await q.CountAsync(c => c.IsMatch);
            var diffCount = totalCount - matchCount;

            var report = CheckReport.Create(
                (BusinessNumber.New("RPT", stampFormat: "yyyyMMdd", stamp: date), date),
                (totalCount, matchCount, diffCount),
                ($"盘点日期：{date:yyyy-MM-dd}，共{totalCount}辆车，一致{matchCount}辆，差异{diffCount}辆", DateTime.Now, operatorId));
            _db.CheckReports.Add(report);
            await _db.SaveChangesAsync();

            //明细归入本报表：一条 UPDATE 完成，避免逐条 AttachToReport 再 SaveChanges
            if (totalCount > 0)
            {
                await _db.CarCheckLists
                    .Where(c => c.CheckDate.Date == day)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.ReportId, report.ReportId));
            }

            await tx.CommitAsync();
            return ToReportDto(report);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    ///<summary>盘点报告列表：报表字段直接投影，不再 Include 操作员与明细</summary>
    public Task<List<CheckReportDto>> GetReports() =>
        _db.CheckReports.AsNoTracking()
            .OrderByDescending(r => r.GenerateTime)
            .Select(r => new CheckReportDto
            {
                ReportId = r.ReportId,
                ReportNo = r.ReportNo,
                CheckDate = r.CheckDate,
                TotalCount = r.TotalCount,
                MatchCount = r.MatchCount,
                DiffCount = r.DiffCount,
                ReportContent = r.ReportContent,
                GenerateTime = r.GenerateTime,
                OperatorId = r.OperatorId,
            })
            .ToListAsync();

    private static CheckReportDto ToReportDto(CheckReport r) => new()
    {
        ReportId = r.ReportId,
        ReportNo = r.ReportNo,
        CheckDate = r.CheckDate,
        TotalCount = r.TotalCount,
        MatchCount = r.MatchCount,
        DiffCount = r.DiffCount,
        ReportContent = r.ReportContent,
        GenerateTime = r.GenerateTime,
        OperatorId = r.OperatorId,
    };
}

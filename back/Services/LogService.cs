using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

public class LogService(AppDbContext db)
{
    private readonly AppDbContext _db=db;

    ///<summary>日志分页查询</summary>
    public async Task<(int Total,List<SystemLogDto> Items)> GetAll(
        (int Page,int Size,int? UserId,DateTime? From,DateTime? To,string? Module,string? Result,string? Keyword) filter)
    {
        //用值元组承载筛选条件
        var page=filter.Page>0?filter.Page:1;
        var size=filter.Size>0?filter.Size:20;

        var q=ApplyLogFilters(_db.SystemLogs.AsQueryable(),filter);

        var total=await q.CountAsync();
        var rows=await ApplyLogSort(q).Skip((page-1)*size).Take(size).ToListAsync();

        return(total,await BuildLogItemsAsync(rows));
    }

    ///<summary>逐条挂载筛选条件</summary>
    private IQueryable<SystemLog> ApplyLogFilters(
        IQueryable<SystemLog> q,
        (int Page,int Size,int? UserId,DateTime? From,DateTime? To,string? Module,string? Result,string? Keyword) filter)
    {
        var(_,_,userId,from,to,module,result,keyword)=filter;

        if(userId.HasValue) q=q.Where(l=>l.UserId==userId);
        if(from.HasValue) q=q.Where(l=>l.OperateTime>=from.Value);
        //前端只传日期，用「小于次日 0 点」才能把当天 23:59 的记录也包含进来
        var endExclusive=to?.Date.AddDays(1);
        if(endExclusive.HasValue) q=q.Where(l=>l.OperateTime<endExclusive.Value);
        if(!string.IsNullOrWhiteSpace(module)) q=q.Where(l=>l.OperateModule==module);
        if(!string.IsNullOrWhiteSpace(result)) q=q.Where(l=>l.OperateResult==result);
        if(!string.IsNullOrWhiteSpace(keyword)) q=ApplyKeywordFilter(q,keyword);

        return q;
    }

    ///<summary>关键字模糊搜索：一次命中「操作内容」或「操作人（姓名/账号）」</summary>
    private IQueryable<SystemLog> ApplyKeywordFilter(IQueryable<SystemLog> q,string keyword)
    {
        var kw=keyword.Trim();
        //关键字同时匹配「操作内容」与「操作人（姓名/账号）」
        var staffIds=_db.SystemUsers.Where(u=>(u.UserName!=null&&u.UserName.Contains(kw))||u.LoginAccount.Contains(kw)).Select(u=>u.UserId);
        var customerIds=_db.Customers.Where(c=>c.FullName.Contains(kw)||(c.LoginAccount!=null&&c.LoginAccount.Contains(kw))).Select(c=>c.CustomerId);
        return q.Where(l=>(l.OperateContent!=null&&l.OperateContent.Contains(kw))||(l.UserId!=null&&(staffIds.Contains(l.UserId.Value)||customerIds.Contains(l.UserId.Value))));
    }

    ///<summary>统一排序口径：日志按操作时间倒序（最新在前）</summary>
    private static IOrderedQueryable<SystemLog> ApplyLogSort(IQueryable<SystemLog> q) =>
        q.OrderByDescending(l=>l.OperateTime);

    ///<summary>拼装当前页 DTO，操作人信息按本页涉及的 ID 做两次批量查询</summary>
    private async Task<List<SystemLogDto>> BuildLogItemsAsync(List<SystemLog> rows)
    {
        //只为本页涉及的 userId 做两次批量查询
        var ids=rows.Where(r=>r.UserId.HasValue).Select(r=>r.UserId!.Value).Distinct().ToList();
        var staff=ids.Count==0?new Dictionary<int,(string? UserName,string LoginAccount)>():await _db.SystemUsers.AsNoTracking().Where(u=>ids.Contains(u.UserId)).Select(u=>new{u.UserId,u.UserName,u.LoginAccount}).ToDictionaryAsync(u=>u.UserId,u=>(u.UserName,u.LoginAccount));
        var customers=ids.Count==0?new Dictionary<int,(string FullName,string? LoginAccount)>():await _db.Customers.AsNoTracking().Where(c=>ids.Contains(c.CustomerId)).Select(c=>new{c.CustomerId,c.FullName,c.LoginAccount}).ToDictionaryAsync(c=>c.CustomerId,c=>(c.FullName,c.LoginAccount));

        return rows.Select(l=>ToDto(l,staff,customers)).ToList();
    }

    ///<summary>单行日志转 DTO：操作人类型按「员工</summary>
    private static SystemLogDto ToDto(
        SystemLog log,
        Dictionary<int,(string? UserName,string LoginAccount)> staff,
        Dictionary<int,(string FullName,string? LoginAccount)> customers)
    {
        var name=string.Empty;
        var account=string.Empty;
        var type="system";
        if(log.UserId is int id)
        {
            if(staff.TryGetValue(id,out var s))
            {
                name=s.UserName??string.Empty;
                account=s.LoginAccount??string.Empty;
                type="staff";
            }
            else if(customers.TryGetValue(id,out var c))
            {
                name=c.FullName??string.Empty;
                account=c.LoginAccount??string.Empty;
                type="customer";
            }
            else
            {
                type="unknown";//账号已删除，日志仍保留（合规要求：日志不可删）
            }
        }

        return new SystemLogDto
        {
            LogId=log.LogId,
            UserId=log.UserId,
            OperatorName=name,
            OperatorAccount=account,
            OperatorType=type,
            OperateModule=log.OperateModule,
            OperateContent=log.OperateContent,
            OperateResult=log.OperateResult,
            IpAddress=log.IpAddress,
            OperateTime=log.OperateTime
        };
    }

    ///<summary>日志中出现过的操作模块（供筛选下拉取值，不硬编码模块名）</summary>
    public async Task<List<string>> GetModules() =>
        await _db.SystemLogs.Where(l=>l.OperateModule!=null).Select(l=>l.OperateModule!).Distinct().OrderBy(m=>m).ToListAsync();

    public async Task WriteLog(int? userId,string content,string module,string result,string? ip=null,int? customerId=null)
    {
        //顾客侧动作（注册、买会员、注销）没有 system_user 账号，操作人留空
        _db.SystemLogs.Add(SystemLog.Create(userId,customerId,content,module,result,ip));
        await _db.SaveChangesAsync();
    }
}

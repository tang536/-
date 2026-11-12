using System.Text.Json;
using ERental.Data;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

///<summary>在线会话登记表</summary>
public sealed class SessionRegistry(AppDbContext db,ILogger<SessionRegistry> logger)
{
    ///<summary>会话键前缀</summary>
    public const string Prefix="sess.";

    private readonly AppDbContext _db=db;
    private readonly ILogger<SessionRegistry> _logger=logger;

    ///<summary>登记表里的一行：当前会话标识</summary>
    private sealed record Payload(string Token,string? Revoked=null);

    ///<summary>账号唯一键：员工与顾客的 ID 各自独立计数，必须带类型前缀区分</summary>
    private static string KeyOf(string userType,int userId)=>$"{Prefix}{userType}.{userId}";

    ///<summary>签发会话</summary>
    public async Task<string> IssueAsync(string userType,int userId)
    {
        var token=Guid.NewGuid().ToString("N");
        await SaveAsync(KeyOf(userType,userId),new Payload(token));
        return token;
    }

    ///<summary>一次取出「是否当前会话+撤销原因」，供中间件与续期校验使用</summary>
    public async Task<(bool Current,string? RevokedReason)> CheckAsync(string userType,int userId,string? sessionToken)
    {
        var entry=await LoadAsync(KeyOf(userType,userId));
        if(entry==null) return(false,null);
        var current=!string.IsNullOrEmpty(sessionToken)&&string.Equals(entry.Token,sessionToken,StringComparison.Ordinal);
        return(current,entry.Revoked);
    }

    ///<summary>退出登录：只撤销当前这一个会话</summary>
    public async Task RevokeAsync(string userType,int userId,string? sessionToken)
    {
        if(string.IsNullOrEmpty(sessionToken)) return;
        var key=KeyOf(userType,userId);
        var entry=await LoadAsync(key);
        if(entry==null) return;
        if(!string.Equals(entry.Token,sessionToken,StringComparison.Ordinal)) return;
        //主动登出不做墓碑
        await _db.SystemConfigs.Where(c=>c.ConfigKey==key&&c.ConfigValue==Serialize(entry)).ExecuteDeleteAsync();
    }

    ///<summary>强制该账号全部会话下线，并留下原因供中间件提示</summary>
    public async Task RevokeAllAsync(string userType,int userId,string reason)=>await SaveAsync(KeyOf(userType,userId),new Payload(string.Empty,reason));

    //── 存取 ──

    private async Task<Payload?> LoadAsync(string key)
    {
        //单点登录校验是每个已认证请求都会走的读路径，且位于授权中间件里、没有任何try/catch 兜底
        var value=await DbConcurrency.RetryTransientAsync(async() =>
        {
            return await _db.SystemConfigs.AsNoTracking().Where(c=>c.ConfigKey==key).Select(c=>c.ConfigValue).FirstOrDefaultAsync();
        },_logger,"会话校验");
        return Deserialize(value);
    }

    ///<summary>写入（存在则改、不存在则插）</summary>
    private async Task SaveAsync(string key,Payload payload)
    {
        var json=Serialize(payload);
        var row=await _db.SystemConfigs.FirstOrDefaultAsync(c=>c.ConfigKey==key);
        if(row==null)
        {
            _db.SystemConfigs.Add(SystemConfig.Create(key,json,"在线会话（系统维护，请勿手工修改）"));
            try
            {
                await _db.SaveChangesAsync();
                return;
            }
            catch(DbUpdateException)
            {
                //并发下已被另一实例插入：脱离跟踪，改走更新分支
                _db.Entry(_db.SystemConfigs.Local.First(c=>c.ConfigKey==key)).State=EntityState.Detached;
            }
        }

        await _db.SystemConfigs.Where(c=>c.ConfigKey==key).ExecuteUpdateAsync(s=>s.SetProperty(c=>c.ConfigValue,json).SetProperty(c=>c.UpdateTime,DateTime.Now));
    }

    private static string Serialize(Payload p)=>JsonSerializer.Serialize(p);

    private static Payload? Deserialize(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw)) return null;
        try{return JsonSerializer.Deserialize<Payload>(raw);}
        catch(JsonException){return null;}   //被手工改坏的键按"无会话"处理，登录一次即自愈
    }
}

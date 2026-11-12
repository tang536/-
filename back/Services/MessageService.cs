using ERental.Data;
using ERental.DTOs;
using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

///<summary>通用消息服务：所有角色（顾客/工作人员）的通知统一落 message 表、统一从这里读写</summary>
public class MessageService(AppDbContext db,ILogger<MessageService> logger)
{
    private readonly AppDbContext _db=db;
    private readonly ILogger<MessageService> _logger=logger;

    ///<summary>推送/更新一条消息（幂等 upsert）</summary>
    public async Task SendAsync(
        (string ReceiverType,int ReceiverId) receiver,
        (string RefType,string? RefId) biz,
        (string Title,string Content) body,
        (string? Level,string? Link,bool KeepReadStateOnChange) options=default)
    {
        var(receiverType,receiverId)=receiver;
        var(refType,refId)=biz;
        //正文整组原样转发给 UpsertAsync，不解构
        var(level,link,keepReadStateOnChange)=options;

        //正文列是 VARCHAR(500)

        try
        {
            //选项元组末尾一位是 InsertIfMissing：首次推送需要允许建行
            await UpsertAsync(receiver,biz,body,(level,link,keepReadStateOnChange,true));
        }
        catch(DbUpdateException)
        {
            //两路同时推同一条通知会撞 uk_msg_biz：对方已经建好这一行
            foreach(var pending in _db.ChangeTracker.Entries<AppMessage>().Where(e=>e.State==EntityState.Added).ToList())
                pending.State=EntityState.Detached;

            try
            {
                //对方已经建过这一行，这里只刷新、不再插入
                await UpsertAsync(receiver,biz,body,(level,link,keepReadStateOnChange,false));
            }
            catch(Exception ex)
            {
                _logger.LogWarning(ex,"推送消息失败（{ReceiverType}/{ReceiverId}/{RefType}/{RefId}）",receiverType,receiverId,refType,refId);
            }
        }
        catch(Exception ex)
        {
            //通知是「尽力而为」的旁路：主链路不能因通知写入失败而回滚
            _logger.LogWarning(ex,"推送消息失败（{ReceiverType}/{ReceiverId}/{RefType}/{RefId}）",receiverType,receiverId,refType,refId);
        }
    }

    private async Task UpsertAsync(
        (string ReceiverType,int ReceiverId) receiver,
        (string RefType,string? RefId) biz,
        (string Title,string Content) body,
        (string? Level,string? Link,bool KeepReadStateOnChange,bool InsertIfMissing) options)
    {
        var(receiverType,receiverId)=receiver;
        var(refType,refId)=biz;
        var(title,content)=body;
        var(level,link,keepReadStateOnChange,insertIfMissing)=options;

        //Create 会把空串收敛成 null，查询条件要跟着对齐
        var key=string.IsNullOrWhiteSpace(refId)?null:refId;

        var existing=await _db.Messages.FirstOrDefaultAsync(m =>
            m.ReceiverType==receiverType&&m.ReceiverId==receiverId&&m.RefType==refType&&m.RefId==key);

        if(existing is null)
        {
            if(!insertIfMissing) return;
            _db.Messages.Add(AppMessage.Create(receiver,(refType,key),body,(level,link)));
        }
        else
        {
            existing.Refresh(title,content,level,link,keepReadStateOnChange);
        }

        await _db.SaveChangesAsync();
    }

    ///<summary>单次返回的消息条数上限</summary>
    public const int MaxPageSize=200;

    ///<summary>默认每页条数</summary>
    public const int DefaultPageSize=50;

    ///<summary>查询某接收人的消息（按时间倒序分页），并附未读计数</summary>
    public async Task<NotificationListDto> ListAsync(string receiverType,int receiverId,int page=1,int pageSize=DefaultPageSize)
    {
        page=page<1?1:page;
        pageSize=pageSize<=0?DefaultPageSize:Math.Min(pageSize,MaxPageSize);

        //AsNoTracking：列表只读，不需要变更跟踪器，省掉每行一份的快照
        var scope=_db.Messages.AsNoTracking().Where(m=>m.ReceiverType==receiverType&&m.ReceiverId==receiverId&&m.DismissedAt==null);

        var rows=await scope.OrderByDescending(m=>m.CreatedAt).Skip((page-1)*pageSize).Take(pageSize).Select(m=>new
            {
                m.MessageId,
                m.RefType,
                m.RefId,
                m.Title,
                m.Content,
                m.Level,
                m.Link,
                m.IsRead,
                m.CreatedAt,
                m.ReadAt
            })
            .ToListAsync();

        var unread=await scope.CountAsync(m=>!m.IsRead);
        var total=await scope.CountAsync();

        return new NotificationListDto
        {
            //Category 由本地方法推导，进不了 SQL
            Items=rows.Select(m=>new NotificationDto
            {
                MessageId=m.MessageId,
                Id=string.IsNullOrEmpty(m.RefId)?m.RefType:$"{m.RefType}-{m.RefId}",
                Level=m.Level??"info",
                Category=NotificationCategories.Of(m.RefType),
                Title=m.Title,
                Description=m.Content,
                Time=m.CreatedAt,
                Link=m.Link,
                IsRead=m.IsRead,
                ReadTime=m.ReadAt
            }).ToList(),
            Unread=unread,
            Total=total,
            Page=page,
            PageSize=pageSize
        };
    }

    ///<summary>标记已读：ids 为空表示「全部标记已读」，只影响该接收人自己的消息</summary>
    public async Task MarkReadAsync(string receiverType,int receiverId,List<int>? ids)
    {
        //点开单条通知：走实体领域方法
        if(ids is{Count: 1})
        {
            var one=await _db.Messages.FirstOrDefaultAsync(m =>
                m.MessageId==ids[0]&&m.ReceiverType==receiverType&&m.ReceiverId==receiverId);
            if(one is null) return;//已不存在或不属于本人：静默，不把「点晚了」变成报错
            one.MarkRead();
            await _db.SaveChangesAsync();
            return;
        }

        var q=_db.Messages.Where(m=>m.ReceiverType==receiverType&&m.ReceiverId==receiverId&&!m.IsRead);
        if(ids is {Count:>0}) q=q.Where(m=>ids.Contains(m.MessageId));

        //一条 UPDATE ... WHERE 取代「查出实体 → 逐行赋值 → SaveChanges」：后者会把每行的正文（content）一并读进内存再按行发 UPDATE
        var now=DateTime.Now;
        await q.ExecuteUpdateAsync(s=>s.SetProperty(m=>m.IsRead,true).SetProperty(m=>m.ReadAt,now));
    }

    ///<summary>删除（忽略）消息：ids 为空表示「清空该接收人的全部消息」</summary>
    public async Task<int> DismissAsync(string receiverType,int receiverId,List<int>? ids)
    {
        //删单条：同 MarkReadAsync
        if(ids is{Count: 1})
        {
            var one=await _db.Messages.FirstOrDefaultAsync(m =>
                m.MessageId==ids[0]&&m.ReceiverType==receiverType&&m.ReceiverId==receiverId);
            if(one is null||one.DismissedAt.HasValue) return 0;
            one.Dismiss();
            await _db.SaveChangesAsync();
            return 1;
        }

        var q=_db.Messages.Where(m=>m.ReceiverType==receiverType&&m.ReceiverId==receiverId&&m.DismissedAt==null);
        if(ids is {Count:>0}) q=q.Where(m=>ids.Contains(m.MessageId));

        var now=DateTime.Now;
        //同样是单条 UPDATE：受影响行数即「实际忽略条数」
        return await q.ExecuteUpdateAsync(s=>s.SetProperty(m=>m.DismissedAt,now));
    }
}

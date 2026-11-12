using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ERental.Services;

namespace ERental.Models;

///<summary>通用消息表（统一通知中心），顾客事件通知与工作人员预警消息共用一张表</summary>
[Table("message")]
public class AppMessage
{
    //接收人类型与字段长度上限
    private const string TypeCustomer="customer";
    private const string TypeStaff="staff";
    private const int MaxRefType=20;
    private const int MaxRefId=60;
    private const int MaxTitle=120;
    private const int MaxContent=500;
    private const int MaxLevel=10;
    private const int MaxLink=120;

    [Key]
    [Column("message_id")]
    public int MessageId{get;init;}

    ///<summary>接收人类型：customer/staff</summary>
    [Required,MaxLength(10)]
    [Column("receiver_type")]
    public string ReceiverType{get;init;}=string.Empty;

    ///<summary>接收人 ID：customer 时为 customer_id</summary>
    [Column("receiver_id")]
    public int ReceiverId{get;init;}

    ///<summary>顾客接收人</summary>
    [Column("receiver_customer_id")]
    public int? ReceiverCustomerId{get;init;}

    [ForeignKey("ReceiverCustomerId")]
    public Customer? ReceiverCustomer{get;init;}

    ///<summary>员工接收人</summary>
    [Column("receiver_staff_id")]
    public int? ReceiverStaffId{get;init;}

    [ForeignKey("ReceiverStaffId")]
    public SystemUser? ReceiverStaff{get;init;}

    ///<summary>业务类型（稳定可复现，便于按场景筛选与统计）：顾客侧</summary>
    [Required,MaxLength(MaxRefType)]
    [Column("ref_type")]
    public string RefType{get;init;}=string.Empty;

    ///<summary>业务实体 ID（如订单号/车辆ID）</summary>
    [MaxLength(MaxRefId)]
    [Column("ref_id")]
    public string? RefId{get;init;}

    [MaxLength(MaxTitle)]
    [Column("title")]
    public string Title{get;private set;}=string.Empty;

    ///<summary>消息正文</summary>
    [MaxLength(MaxContent)]
    [Column("content")]
    public string Content{get;private set;}=string.Empty;

    ///<summary>预警级别（员工侧预警用）：danger/warning/info/success</summary>
    [MaxLength(MaxLevel)]
    [Column("level")]
    public string? Level{get;private set;}

    ///<summary>点击后跳转的前端路由（如 /orders、/my-orders）</summary>
    [MaxLength(MaxLink)]
    [Column("link")]
    public string? Link{get;private set;}

    ///<summary>是否已读（0=未读，1=已读）：只能经 MarkRead 改写，保证与 ReadAt 同时落地</summary>
    [Column("is_read")]
    public bool IsRead{get;private set;}

    [Column("created_at")]
    public DateTime CreatedAt{get;private set;}=DateTime.Now;

    [Column("read_at")]
    public DateTime? ReadAt{get;private set;}

    ///<summary>忽略时间（员工侧「删除」通知=软删）：非空即对该接收人隐藏</summary>
    [Column("dismissed_at")]
    public DateTime? DismissedAt{get;private set;}

    ///<summary>站内消息</summary>
    public static AppMessage Create(
        (string ReceiverType,int ReceiverId) receiver,
        (string RefType,string? RefId) biz,
        (string Title,string Content) body,
        (string? Level,string? Link) options=default)
    {
        var(receiverType,receiverId)=receiver;
        var(refType,refId)=biz;
        var(title,content)=body;
        var(level,link)=options;

        if(receiverType is not(TypeCustomer or TypeStaff)) throw new BusinessException("接收人类型只能是 customer 或 staff");
        if(receiverId<=0) throw new BusinessException("接收人 ID 无效");
        if(string.IsNullOrWhiteSpace(refType)||refType.Length > MaxRefType)
            throw new BusinessException("消息业务类型无效");
        if((refId?.Length??0) > MaxRefId) throw new BusinessException("消息业务 ID 过长");
        if((level?.Length??0) > MaxLevel) throw new BusinessException("消息级别值过长");
        if((link?.Length??0) > MaxLink) throw new BusinessException("消息跳转链接过长");

        return new AppMessage
        {
            ReceiverType=receiverType,
            ReceiverId=receiverId,
            ReceiverCustomerId=receiverType==TypeCustomer?receiverId:null,
            ReceiverStaffId=receiverType==TypeStaff?receiverId:null,
            RefType=refType,
            RefId=string.IsNullOrWhiteSpace(refId)?null:refId,
            Title=Clip(title,MaxTitle),
            Content=Clip(content,MaxContent),
            Level=string.IsNullOrWhiteSpace(level)?null:level,
            Link=string.IsNullOrWhiteSpace(link)?null:link,
        };
    }

    ///<summary>同一条业务通知再次推送时刷新正文（幂等 upsert 的「已存在」分支）</summary>
    public void Refresh(string title,string content,string? level,string? link,bool keepReadState=false)
    {
        var contentChanged=!string.Equals(Title,Clip(title,MaxTitle),StringComparison.Ordinal)
                          ||!string.Equals(Content,Clip(content,MaxContent),StringComparison.Ordinal);

        Title=Clip(title,MaxTitle);
        Content=Clip(content,MaxContent);
        Level=string.IsNullOrWhiteSpace(level)?null:level;
        Link=string.IsNullOrWhiteSpace(link)?null:link;

        if(keepReadState||!contentChanged) return;

        IsRead=false;
        ReadAt=null;
        CreatedAt=DateTime.Now;//内容变了就重新排到收件箱顶部
    }

    ///<summary>标记已读</summary>
    public void MarkRead(DateTime? at=null)
    {
        if(IsRead) return;
        IsRead=true;
        ReadAt=at??DateTime.Now;
    }

    ///<summary>忽略（软删）：只写一次忽略时间，重复点击不改变原有排序位置</summary>
    public void Dismiss(DateTime? at=null)
    {
        if(DismissedAt.HasValue) return;
        DismissedAt=at??DateTime.Now;
    }

    private static string Clip(string? text,int max)
    {
        if(string.IsNullOrEmpty(text)) return string.Empty;
        if(text.Length<=max) return text;
        var cut=max;
        if(char.IsHighSurrogate(text[cut-1])) cut--;
        return text[..cut];
    }
}

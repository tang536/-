using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using ERental.Data;

namespace ERental.Models;

///<summary>客户（租赁方） 注册阶段仅采集「登录用户名+密码」（最小必要原则，个人信息保护法）</summary>
[Table("customer")]
public class Customer
{
    [Key]
    [Column("customer_id")]
    public int CustomerId { get; init; }

    ///<summary>注册登录用户名（顾客自主注册时填写，全局唯一）</summary>
    [MaxLength(20)]
    [Column("login_account")]
    public string? LoginAccount { get; private set; }

    ///<summary>身份证号：注册时可不填，租车下单前必须补齐</summary>
    [MaxLength(18)]
    [Column("id_card")]
    public string? IdCard { get; private set; }

    ///<summary>姓名/昵称：注册时默认取用户名，租车时可更新为真实姓名</summary>
    [Required, MaxLength(20)]
    [Column("full_name")]
    public string FullName { get; private set; } = string.Empty;

    ///<summary>联系电话：注册时可不填，租车下单前必须补齐</summary>
    [MaxLength(11)]
    [Column("contact_phone")]
    public string? ContactPhone { get; private set; }

    ///<summary>登录密码（BCrypt 哈希），任何接口都不允许回传给前端</summary>
    [JsonIgnore]
    [MaxLength(100)]
    [Column("login_password")]
    public string? LoginPassword { get; private set; }

    [MaxLength(20)]
    [Column("member_level")]
    public string MemberLevel { get; private set; } = "普通会员";

    ///<summary>会员等级权益外键：等级名用于展示与筛选，权益以本外键为准</summary>
    [Column("member_rights_id")]
    public int? MemberRightsId { get; private set; }

    ///<summary>等级权益导航：折扣、最低租赁数、自助购买价都从这条记录取，不在客户表冗余</summary>
    [ForeignKey(nameof(MemberRightsId))]
    public MemberRights? MemberRights { get; private init; }

    ///<summary>会员折扣</summary>
    [NotMapped]
    public decimal MemberDiscount { get; private set; } = 1.00m;

    ///<summary>账号状态：正常/已注销</summary>
    [MaxLength(10)]
    [Column("account_status")]
    public string AccountStatus { get; private set; } = "正常";

    [Column("create_time")]
    public DateTime CreateTime { get; init; } = AppClock.Now;

    ///<summary>乐观并发版本号：客户档案同时暴露给前台（客户管理）与管理员（会员调整/注销）</summary>
    [Column("version")]
    [ConcurrencyCheck]
    public int Version { get; private set; } = 1;

    //导航集合只给 EF 用
    private readonly List<RentalOrder> _rentalOrders = [];
    [JsonIgnore] public IReadOnlyCollection<RentalOrder> RentalOrders => _rentalOrders;
    //该顾客收到的站内消息
    private readonly List<AppMessage> _messages = [];
    [JsonIgnore] public IReadOnlyCollection<AppMessage> Messages => _messages;
    ///<summary>租车资料是否已完善（身份证号+手机号），下单前置校验用</summary>
    [NotMapped]
    public bool ProfileCompleted => !string.IsNullOrWhiteSpace(IdCard) && !string.IsNullOrWhiteSpace(ContactPhone);

    ///<summary>顾客线上自助注册：只有登录名与密码，身份证/手机号留到租车环节采集</summary>
    public static Customer Register(string loginAccount, string? fullName, string? passwordHash) => new()
    {
        LoginAccount = loginAccount.Trim(),
        FullName = string.IsNullOrWhiteSpace(fullName) ? loginAccount.Trim() : fullName.Trim(),
        LoginPassword = string.IsNullOrWhiteSpace(passwordHash) ? null : passwordHash,
        MemberLevel = "普通会员",
        AccountStatus = "正常",
        Version = 1,
    };

    ///<summary>前台代客建档（线下客户没有线上账号，登录名与密码留空释放唯一索引）</summary>
    public static Customer WalkIn(string fullName, string idCard, string? contactPhone) => new()
    {
        LoginAccount = null,
        LoginPassword = null,
        FullName = fullName.Trim(),
        IdCard = idCard.Trim(),
        ContactPhone = string.IsNullOrWhiteSpace(contactPhone) ? null : contactPhone.Trim(),
        MemberLevel = "普通会员",
        AccountStatus = "正常",
        Version = 1,
    };

    ///<summary>补全/修改实名资料：传空表示本次不修改该字段（与前端「只提交改过的项」一致）</summary>
    public void UpdateProfile(string? fullName, string? idCard, string? contactPhone)
    {
        if (!string.IsNullOrWhiteSpace(fullName)) FullName = fullName.Trim();
        if (!string.IsNullOrWhiteSpace(idCard)) IdCard = idCard.Trim();
        if (!string.IsNullOrWhiteSpace(contactPhone)) ContactPhone = contactPhone.Trim();
    }

    ///<summary>设置/释放登录用户名</summary>
    public void AssignLoginAccount(string? account) =>
        LoginAccount = string.IsNullOrWhiteSpace(account) ? null : account.Trim();

    ///<summary>写入密码哈希</summary>
    public void SetPasswordHash(string? hash) =>
        LoginPassword = string.IsNullOrWhiteSpace(hash) ? null : hash;

    ///<summary>调整会员等级（折扣不落档案，等级名以权益表为准，由调用方先校验存在性）</summary>
    public void ChangeMemberLevel(string level)
    {
        if (string.IsNullOrWhiteSpace(level)) return;
        var normalized = level.Trim();
        //只改了名字、没重新绑定权益行时，旧外键会指向已不匹配的等级，先松开
        if (!string.Equals(MemberLevel, normalized, StringComparison.Ordinal)) MemberRightsId = null;
        MemberLevel = normalized;
    }

    ///<summary>调整会员等级并绑定权益行</summary>
    public void ChangeMemberLevel(MemberRights rights)
    {
        ArgumentNullException.ThrowIfNull(rights);
        MemberLevel = rights.MemberLevel;
        MemberRightsId = rights.RightsId;
        SetMemberDiscount(rights.Discount);
    }

    ///<summary>按等级权益回填展示折扣</summary>
    public void SetMemberDiscount(decimal discount) =>
        MemberDiscount = discount is >= 0.5m and <= 1m ? discount : 1.00m;

    ///<summary>乐观并发自增（在自身当前版本上前进一格）</summary>
    public void BumpVersion() => Version++;

    ///<summary>以提交方看到的版本号为基准自增：基准对不上时 EF 的并发校验会拒绝写入</summary>
    public void BumpVersion(int submittedVersion) => Version = submittedVersion + 1;

    ///<summary>顾客自主注销：置「已注销」并释放用户名与实名信息</summary>
    public void DeactivateAccount()
    {
        AccountStatus = "已注销";
        LoginAccount = null;
        LoginPassword = null;
        IdCard = null;
        ContactPhone = null;
        FullName = $"已注销用户#{CustomerId}";
    }
}

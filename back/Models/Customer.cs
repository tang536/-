using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace ERental.Models;

/// <summary>
/// 客户（租赁方）
/// 注册阶段仅采集「登录用户名 + 密码」（最小必要原则，个人信息保护法）；
/// 身份证号与手机号在租车下单环节强制采集。
/// </summary>
[Table("customer")]
public class Customer
{
    [Key]
    [Column("customer_id")]
    public int CustomerId { get; init; }

    /// <summary>注册登录用户名（顾客自主注册时填写，全局唯一）</summary>
    [MaxLength(20)]
    [Column("login_account")]
    public string? LoginAccount { get; set; }

    /// <summary>身份证号：注册时可不填，租车下单前必须补齐</summary>
    [MaxLength(18)]
    [Column("id_card")]
    public string? IdCard { get; set; }

    /// <summary>姓名/昵称：注册时默认取用户名，租车时可更新为真实姓名</summary>
    [Required, MaxLength(20)]
    [Column("full_name")]
    public string FullName { get; set; } = string.Empty;

    /// <summary>联系电话：注册时可不填，租车下单前必须补齐</summary>
    [MaxLength(11)]
    [Column("contact_phone")]
    public string? ContactPhone { get; set; }

    /// <summary>登录密码（BCrypt 哈希），任何接口都不允许回传给前端</summary>
    [JsonIgnore]
    [MaxLength(100)]
    [Column("login_password")]
    public string? LoginPassword { get; set; }

    [MaxLength(20)]
    [Column("member_level")]
    public string MemberLevel { get; set; } = "普通会员";

    /// <summary>
    /// 会员折扣（3NF：不落库）。折扣唯一真源在 member_rights（customer → member_level → rights.discount
    /// 是传递依赖，冗余存储会迫使类型改折扣时批量同步档案）。此属性仅为 API 输出保留，
    /// 由服务层按等级关联权益表填值，缺等级时按不打折（1.00）处理。
    /// </summary>
    [NotMapped]
    public decimal MemberDiscount { get; set; } = 1.00m;

    /// <summary>
    /// 账号状态：正常 / 已注销。
    /// 顾客自助注销时不物理删除记录——历史订单、结算、日志都要能追溯到本人
    /// （需求 7.1：数据不可删除、不可篡改，全流程可追溯）。
    /// </summary>
    [MaxLength(10)]
    [Column("account_status")]
    public string AccountStatus { get; set; } = "正常";

    [Column("create_time")]
    public DateTime CreateTime { get; init; } = DateTime.Now;

    /// <summary>
    /// 乐观并发版本号：客户档案同时暴露给前台（客户管理）与管理员（会员调整/注销），
    /// 编辑时以打开表单时看到的版本号为基准提交，基准对不上即拒绝写入——
    /// 否则后提交方会悄悄覆盖先提交方的修改。所有写路径必须 Version++ 后再保存。
    /// </summary>
    [Column("version")]
    [ConcurrencyCheck]
    public int Version { get; set; } = 1;

    public ICollection<RentalOrder> RentalOrders { get; init; } = new List<RentalOrder>();

    /// <summary>租车资料是否已完善（身份证号 + 手机号），下单前置校验用</summary>
    [NotMapped]
    public bool ProfileCompleted => !string.IsNullOrWhiteSpace(IdCard) && !string.IsNullOrWhiteSpace(ContactPhone);
}

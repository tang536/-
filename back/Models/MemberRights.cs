using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

[Table("member_rights")]
public class MemberRights
{
    [Key]
    [Column("rights_id")]
    public int RightsId { get; init; }

    [Required, MaxLength(20)]
    [Column("member_level")]
    public string MemberLevel { get; init; } = string.Empty;

    [Column("discount", TypeName = "decimal(3,2)")]
    public decimal Discount { get; private set; } = 1.00m;

    [Column("min_rent_count")]
    public int? MinRentCount { get; private set; }

    /// <summary>自助购买价（元）。NULL 表示该等级未开放购买（如普通会员）；价格为真源，不再走系统配置。</summary>
    [Column("purchase_price", TypeName = "decimal(10,2)")]
    public decimal? PurchasePrice { get; private set; }

    [MaxLength(200)]
    [Column("rights_desc")]
    public string? RightsDesc { get; private set; }

    // ------------------------------------------------------------------ 工厂
    /// <summary>新增等级权益（每级一行）。等级名是天然主键语义，建后不改，只改折扣与权益内容。</summary>
    public static MemberRights Create(string memberLevel, decimal discount, int? minRentCount,
        decimal? purchasePrice, string? rightsDesc) => new()
        {
            MemberLevel = memberLevel.Trim(),
            Discount = discount,
            MinRentCount = minRentCount,
            PurchasePrice = purchasePrice,
            RightsDesc = string.IsNullOrWhiteSpace(rightsDesc) ? null : rightsDesc.Trim(),
        };

    // -------------------------------------------------------------- 领域方法
    /// <summary>
    /// 维护等级权益（管理员「会员权益设置」，用况7 扩展4）：折扣率、最低租赁数、自助购买价与说明一起覆盖。
    /// 价格与折扣是「即时生效」的——新单按新值计价，历史订单金额不受影响。
    /// </summary>
    public void UpdateRights(decimal discount, int? minRentCount, decimal? purchasePrice, string? rightsDesc)
    {
        Discount = discount;
        MinRentCount = minRentCount;
        PurchasePrice = purchasePrice;
        RightsDesc = string.IsNullOrWhiteSpace(rightsDesc) ? null : rightsDesc.Trim();
    }
}

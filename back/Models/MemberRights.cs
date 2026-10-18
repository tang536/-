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
    public decimal Discount { get; set; } = 1.00m;

    [Column("min_rent_count")]
    public int? MinRentCount { get; set; }

    /// <summary>自助购买价（元）。NULL 表示该等级未开放购买（如普通会员）；价格为真源，不再走系统配置。</summary>
    [Column("purchase_price", TypeName = "decimal(10,2)")]
    public decimal? PurchasePrice { get; set; }

    [MaxLength(200)]
    [Column("rights_desc")]
    public string? RightsDesc { get; set; }
}

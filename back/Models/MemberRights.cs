using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using ERental.Services;
namespace ERental.Models;

[Table("member_rights")]
public class MemberRights
{
    [Key]
    [Column("rights_id")]
    public int RightsId { get; init; }

    [Required, MaxLength(20)]
    [Column("member_level")]
    public string MemberLevel { get; private set; } = string.Empty;

    [Column("discount", TypeName = "decimal(3,2)")]
    public decimal Discount { get; private set; } = 1.00m;

    [Column("min_rent_count")]
    public int? MinRentCount { get; private set; }

    ///<summary>自助购买价（元）</summary>
    [Column("purchase_price", TypeName = "decimal(10,2)")]
    public decimal? PurchasePrice { get; private set; }

    [MaxLength(200)]
    [Column("rights_desc")]
    public string? RightsDesc { get; private set; }

    //该等级下的客户
    private readonly List<Customer> _customers = [];
    [JsonIgnore] public IReadOnlyCollection<Customer> Customers => _customers;
    ///<summary>新增等级权益（每级一行）</summary>
    public static MemberRights Create(string memberLevel, decimal discount, int? minRentCount,
        decimal? purchasePrice, string? rightsDesc)
    {
        if (string.IsNullOrWhiteSpace(memberLevel)) throw new BusinessException("会员等级名称不能为空");
        ValidateRights(discount, minRentCount, purchasePrice);

        return new MemberRights
        {
            MemberLevel = memberLevel.Trim(),
            Discount = discount,
            MinRentCount = minRentCount,
            PurchasePrice = purchasePrice,
            RightsDesc = string.IsNullOrWhiteSpace(rightsDesc) ? null : rightsDesc.Trim(),
        };
    }

    ///<summary>权益不变式：折扣 0.5~1（5 折到不打折），最低租赁数与购买价不得为负</summary>
    private static void ValidateRights(decimal discount, int? minRentCount, decimal? purchasePrice)
    {
        if (discount is < 0.5m or > 1m) throw new BusinessException("折扣需在 0.5 ~ 1 之间（5 折 ~ 不打折）");
        if (minRentCount is < 0) throw new BusinessException("最低租赁数不能为负数");
        if (purchasePrice is < 0) throw new BusinessException("购买价不能为负数");
    }

    ///<summary>维护等级权益：折扣率、最低租赁数、自助购买价与说明一起覆盖</summary>
    public void UpdateRights(decimal discount, int? minRentCount, decimal? purchasePrice, string? rightsDesc)
    {
        ValidateRights(discount, minRentCount, purchasePrice);
        Discount = discount;
        MinRentCount = minRentCount;
        PurchasePrice = purchasePrice;
        RightsDesc = string.IsNullOrWhiteSpace(rightsDesc) ? null : rightsDesc.Trim();
    }
}

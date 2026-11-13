using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
namespace ERental.Models;

[Table("renewal_record")]
public class RenewalRecord
{
    [Key]
    [Column("renewal_id")]
    public int RenewalId { get; init; }

    [Column("order_id")]
    public int OrderId { get; private set; }

    [ForeignKey("OrderId")]
    public RentalOrder? RentalOrder { get; init; }

    [Column("renewal_days")]
    public int RenewalDays { get; private set; }

    [Column("renewal_fee", TypeName = "decimal(10,2)")]
    public decimal RenewalFee { get; private set; }

    [Column("old_end_time")]
    public DateTime OldEndTime { get; private set; }

    [Column("new_end_time")]
    public DateTime NewEndTime { get; private set; }

    [Column("renewal_time")]
    public DateTime RenewalTime { get; private set; } = DateTime.Now;

    [Column("operator_id")]
    public int? OperatorId { get; private set; }
}

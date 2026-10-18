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
    public int OrderId { get; init; }

    [ForeignKey("OrderId")]
    public RentalOrder? RentalOrder { get; init; }

    [Column("renewal_days")]
    public int RenewalDays { get; init; }

    [Column("renewal_fee", TypeName = "decimal(10,2)")]
    public decimal RenewalFee { get; init; }

    [Column("old_end_time")]
    public DateTime OldEndTime { get; init; }

    [Column("new_end_time")]
    public DateTime NewEndTime { get; init; }

    [Column("renewal_time")]
    public DateTime RenewalTime { get; init; } = DateTime.Now;

    [Column("operator_id")]
    public int? OperatorId { get; init; }
}

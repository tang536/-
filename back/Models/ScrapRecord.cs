using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

[Table("scrap_record")]
public class ScrapRecord
{
    [Key]
    [Column("scrap_id")]
    public int ScrapId { get; init; }

    [Column("vehicle_id")]
    public int VehicleId { get; init; }

    [ForeignKey("VehicleId")]
    public Vehicle? Vehicle { get; init; }

    [MaxLength(200)]
    [Column("scrap_reason")]
    public string? ScrapReason { get; init; }

    [Column("scrap_time")]
    public DateTime ScrapTime { get; init; } = DateTime.Now;

    [Column("operator_id")]
    public int? OperatorId { get; init; }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

[Table("car_check_list")]
public class CarCheckList
{
    [Key]
    [Column("check_id")]
    public int CheckId { get; init; }

    [Required, MaxLength(30)]
    [Column("check_no")]
    public string CheckNo { get; init; } = string.Empty;

    [Column("check_date")]
    public DateTime CheckDate { get; init; } = DateTime.Now;

    [Column("vehicle_id")]
    public int VehicleId { get; init; }

    [ForeignKey("VehicleId")]
    public Vehicle? Vehicle { get; init; }

    [MaxLength(10)]
    [Column("system_status")]
    public string SystemStatus { get; init; } = string.Empty;

    [MaxLength(10)]
    [Column("actual_status")]
    public string ActualStatus { get; init; } = string.Empty;

    [Column("is_match")]
    public bool IsMatch { get; init; }

    [MaxLength(200)]
    [Column("difference_desc")]
    public string? DifferenceDesc { get; init; }

    [Column("operator_id")]
    public int? OperatorId { get; init; }
}

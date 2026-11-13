using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ERental.Services;
using ERental.Data;
namespace ERental.Models;

[Table("scrap_record")]
public class ScrapRecord
{
    [Key]
    [Column("scrap_id")]
    public int ScrapId { get; init; }

    [Column("vehicle_id")]
    public int VehicleId { get; private set; }

    [ForeignKey("VehicleId")]
    public Vehicle? Vehicle { get; init; }

    [MaxLength(200)]
    [Column("scrap_reason")]
    public string? ScrapReason { get; private set; }

    [Column("scrap_time")]
    public DateTime ScrapTime { get; private set; } = AppClock.Now;

    [Column("operator_id")]
    public int? OperatorId { get; private set; }

    ///<summary>报废留痕：车辆 ID 与原因长度在实体内校验</summary>
    public static ScrapRecord Create(int vehicleId, string? scrapReason, int? operatorId, DateTime? scrapTime = null)
    {
        if (vehicleId <= 0) throw new BusinessException("报废记录必须关联有效车辆");
        if ((scrapReason?.Length ?? 0) > 200) throw new BusinessException("报废原因不能超过 200 个字符");

        return new ScrapRecord
        {
            VehicleId = vehicleId,
            ScrapReason = string.IsNullOrWhiteSpace(scrapReason) ? null : scrapReason.Trim(),
            OperatorId = operatorId,
            ScrapTime = scrapTime ?? AppClock.Now,
        };
    }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

[Table("check_report")]
public class CheckReport
{
    [Key]
    [Column("report_id")]
    public int ReportId { get; init; }

    [Required, MaxLength(30)]
    [Column("report_no")]
    public string ReportNo { get; init; } = string.Empty;

    [Column("check_date")]
    public DateTime CheckDate { get; init; } = DateTime.Now;

    [Column("total_count")]
    public int TotalCount { get; init; }

    [Column("match_count")]
    public int MatchCount { get; init; }

    [Column("diff_count")]
    public int DiffCount { get; init; }

    [MaxLength(65535)]
    [Column("report_content", TypeName = "text")]
    public string? ReportContent { get; init; }

    [Column("generate_time")]
    public DateTime GenerateTime { get; init; } = DateTime.Now;

    [Column("operator_id")]
    public int? OperatorId { get; init; }
}

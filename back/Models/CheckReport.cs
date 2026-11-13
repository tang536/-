using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using ERental.Services;

namespace ERental.Models;

[Table("check_report")]
public class CheckReport
{
    [Key]
    [Column("report_id")]
    public int ReportId { get; init; }

    [Required, MaxLength(30)]
    [Column("report_no")]
    public string ReportNo { get; private set; } = string.Empty;

    [Column("check_date")]
    public DateTime CheckDate { get; private set; } = DateTime.Now;

    [Column("total_count")]
    public int TotalCount { get; private set; }

    [Column("match_count")]
    public int MatchCount { get; private set; }

    [Column("diff_count")]
    public int DiffCount { get; private set; }

    [MaxLength(65535)]
    [Column("report_content", TypeName = "text")]
    public string? ReportContent { get; private set; }

    [Column("generate_time")]
    public DateTime GenerateTime { get; private set; } = DateTime.Now;

    [Column("operator_id")]
    public int? OperatorId { get; private set; }

    ///<summary>生成人（盘点报表由谁出具，用于责任追溯）</summary>
    [ForeignKey("OperatorId")]
    public SystemUser? Operator { get; init; }

    ///<summary>本报表覆盖的盘点明细（一次盘点生成一份报表，报表与明细是一对多）</summary>
    private readonly List<CarCheckList> _details = [];
    [JsonIgnore] public IReadOnlyCollection<CarCheckList> Details => _details;
    ///<summary>盘点报表</summary>
    public static CheckReport Create(
        (string ReportNo, DateTime CheckDate) basic,
        (int TotalCount, int MatchCount, int DiffCount) stats,
        (string? ReportContent, DateTime GenerateTime, int? OperatorId) meta)
    {
        var (reportNo, checkDate) = basic;
        var (totalCount, matchCount, diffCount) = stats;
        var (reportContent, generateTime, operatorId) = meta;

        if (string.IsNullOrWhiteSpace(reportNo)) throw new BusinessException("盘点报表编号不能为空");
        if (totalCount < 0 || matchCount < 0 || diffCount < 0) throw new BusinessException("盘点统计数不能为负数");
        if (totalCount != matchCount + diffCount) throw new BusinessException("盘点总数必须等于一致数与差异数之和");

        return new CheckReport
        {
            ReportNo = reportNo.Trim(),
            CheckDate = checkDate,
            TotalCount = totalCount,
            MatchCount = matchCount,
            DiffCount = diffCount,
            ReportContent = reportContent,
            GenerateTime = generateTime,
            OperatorId = operatorId,
        };
    }
}

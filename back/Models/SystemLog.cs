using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

[Table("system_log")]
public class SystemLog
{
    [Key]
    [Column("log_id")]
    public int LogId { get; init; }

    [Column("user_id")]
    public int? UserId { get; init; }

    [MaxLength(200)]
    [Column("operate_content")]
    public string? OperateContent { get; init; }

    [Column("operate_time")]
    public DateTime OperateTime { get; init; } = DateTime.Now;

    [MaxLength(10)]
    [Column("operate_result")]
    public string? OperateResult { get; init; }

    [MaxLength(30)]
    [Column("operate_module")]
    public string? OperateModule { get; init; }

    [MaxLength(50)]
    [Column("ip_address")]
    public string? IpAddress { get; init; }
}

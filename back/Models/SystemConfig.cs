using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERental.Models;

[Table("system_config")]
public class SystemConfig
{
    [Key]
    [Column("config_id")]
    public int ConfigId { get; init; }

    [Required, MaxLength(50)]
    [Column("config_key")]
    public string ConfigKey { get; init; } = string.Empty;

    /// <summary>
    /// 配置值。用 TEXT 而非 VARCHAR(200)：支付凭证（应用私钥约 1500 字符）需要由管理员
    /// 在管理员端填写后存这里，200 字符装不下。普通业务参数仍由 SystemConfigService 限制在 200 以内。
    /// </summary>
    [MaxLength(65535)]
    [Column("config_value", TypeName = "text")]
    public string? ConfigValue { get; set; }

    [MaxLength(100)]
    [Column("config_desc")]
    public string? ConfigDesc { get; set; }

    [Column("update_time")]
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

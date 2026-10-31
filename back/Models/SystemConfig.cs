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

    /// <summary>配置值。用 TEXT 而非 VARCHAR(200)：支付凭证（应用私钥约 1500 字符）需要由管理员 在管理员端…</summary>
    [MaxLength(65535)]
    [Column("config_value", TypeName = "text")]
    public string? ConfigValue { get; private set; }

    [MaxLength(100)]
    [Column("config_desc")]
    public string? ConfigDesc { get; private set; }

    [Column("update_time")]
    public DateTime UpdateTime { get; private set; } = DateTime.Now;

    // ------------------------------------------------------------------ 工厂
    /// <summary>新建配置项</summary>
    public static SystemConfig Create(string configKey, string? configValue, string? configDesc) => new()
        {
            ConfigKey = configKey.Trim(),
            ConfigValue = configValue,
            ConfigDesc = string.IsNullOrWhiteSpace(configDesc) ? null : configDesc.Trim(),
            UpdateTime = DateTime.Now,
        };

    // -------------------------------------------------------------- 领域方法
    /// <summary>写配置值并刷新修改时间</summary>
    public void SetValue(string? value)
    {
        ConfigValue = value;
        UpdateTime = DateTime.Now;
    }

    /// <summary>改配置说明（不刷新修改时间：说明是给人看的注记，不参与业务生效判定）</summary>
    public void SetDescription(string? desc) =>
        ConfigDesc = string.IsNullOrWhiteSpace(desc) ? null : desc.Trim();
}

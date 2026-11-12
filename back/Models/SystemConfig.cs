using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ERental.Services;
namespace ERental.Models;

[Table("system_config")]
public class SystemConfig
{
    [Key]
    [Column("config_id")]
    public int ConfigId{get;init;}

    [Required,MaxLength(50)]
    [Column("config_key")]
    public string ConfigKey{get;private set;}=string.Empty;

    ///<summary>配置值</summary>
    [MaxLength(65535)]
    [Column("config_value",TypeName="text")]
    public string? ConfigValue{get;private set;}

    [MaxLength(100)]
    [Column("config_desc")]
    public string? ConfigDesc{get;private set;}

    [Column("update_time")]
    public DateTime UpdateTime{get;private set;}=DateTime.Now;

    ///<summary>新建配置项：配置键是全局唯一键，必须非空且去空格</summary>
    public static SystemConfig Create(string configKey,string? configValue,string? configDesc)
    {
        if(string.IsNullOrWhiteSpace(configKey)) throw new BusinessException("配置键不能为空");

        return new SystemConfig
        {
            ConfigKey=configKey.Trim(),
            ConfigValue=configValue,
            ConfigDesc=string.IsNullOrWhiteSpace(configDesc)?null:configDesc.Trim(),
            UpdateTime=DateTime.Now,
        };
    }

    ///<summary>写配置值并刷新修改时间</summary>
    public void SetValue(string? value)
    {
        ConfigValue=value;
        UpdateTime=DateTime.Now;
    }

    ///<summary>改配置说明（不刷新修改时间：说明是给人看的注记，不参与业务生效判定）</summary>
    public void SetDescription(string? desc) =>
        ConfigDesc=string.IsNullOrWhiteSpace(desc)?null:desc.Trim();
}

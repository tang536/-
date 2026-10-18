using ERental.Models;

namespace ERental.Services;

/// <summary>
/// 数值型系统配置的安全读取。
///
/// 背景：「系统配置」页允许管理员改任意配置项的文本值，而日租金、押金、逾期费率这些是
/// **数值型**配置。一旦被写成非数字（例如「不是数字」），下单、结算、续租三条主链会在
/// <c>decimal.Parse</c> 处抛 .NET 英文异常
/// （<i>The input string '不是数字' was not in a correct format.</i>）并原样甩给用户，
/// 等于把整个租赁业务锁死。
///
/// 防御分两层：
///   ① 写入层 <see cref="SystemConfigService.Update"/> 拦截非法数值，不让脏值进库；
///   ② 读取层（本类）做 TryParse，万一历史库里已有脏值，也只会得到一句中文可读提示，
///      而不是英文异常；同时缺少配置时回退到默认值，保证业务不中断。
/// </summary>
public static class ConfigValue
{
    /// <summary>读取小数型配置；缺失或为空时用 fallback，格式非法时抛中文提示。</summary>
    public static decimal Decimal(IEnumerable<SystemConfig> configs, string key, decimal fallback)
    {
        var raw = configs.FirstOrDefault(c => c.ConfigKey == key)?.ConfigValue;
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return decimal.TryParse(raw, out var v)
            ? v
            : throw new Exception($"系统配置「{key}」当前值为“{raw}”，不是有效数字，请先在「系统配置」页面修正后再操作");
    }

    /// <summary>读取整数型配置；缺失或为空时用 fallback，格式非法时抛中文提示。</summary>
    public static int Int(IEnumerable<SystemConfig> configs, string key, int fallback)
    {
        var raw = configs.FirstOrDefault(c => c.ConfigKey == key)?.ConfigValue;
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw, out var v)
            ? v
            : throw new Exception($"系统配置「{key}」当前值为“{raw}”，不是有效整数，请先在「系统配置」页面修正后再操作");
    }
}

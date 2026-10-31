using ERental.Models;

namespace ERental.Services;

/// <summary>数值型系统配置的安全读取</summary>
public static class ConfigValue
{
    /// <summary>读取小数型配置</summary>
    public static decimal Decimal(IEnumerable<SystemConfig> configs, string key, decimal fallback)
    {
        var raw = configs.FirstOrDefault(c => c.ConfigKey == key)?.ConfigValue;
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return decimal.TryParse(raw, out var v)
            ? v
            : throw new Exception($"系统配置「{key}」当前值为“{raw}”，不是有效数字，请先在「系统配置」页面修正后再操作");
    }

    /// <summary>读取整数型配置</summary>
    public static int Int(IEnumerable<SystemConfig> configs, string key, int fallback)
    {
        var raw = configs.FirstOrDefault(c => c.ConfigKey == key)?.ConfigValue;
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw, out var v)
            ? v
            : throw new Exception($"系统配置「{key}」当前值为“{raw}”，不是有效整数，请先在「系统配置」页面修正后再操作");
    }
}

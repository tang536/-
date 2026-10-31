using ERental.Data;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>车辆计价：每辆车有自己的日租金与押金（vehicle</summary>
public static class PricingResolver
{
    public const decimal FallbackDailyRent = 30m;
    public const decimal FallbackDeposit = 500m;

    /// <summary>一天按多少小时折算（与「日租金 ÷ 24 得小时价」是同一个常量）</summary>
    private const int HoursPerDay = 24;

    /// <summary>日租金换算成小时价，四舍五入到分</summary>
    public static decimal HourlyRent(decimal dailyRent) =>
        Math.Round(dailyRent / HoursPerDay, 2);

    /// <summary>租期折算成计费小时数，向上取整，最少 1 小时（用 Ceiling 而非 Truncate，避免短租亏钱）</summary>
    public static int CalcHours(DateTime start, DateTime end)
    {
        var hours = (int)Math.Ceiling((end - start).TotalHours);
        return hours < 1 ? 1 : hours;
    }

    /// <summary>解析某辆车的计价</summary>
    private static async Task<(decimal DailyRent, decimal Deposit)> ResolveAsync(
        AppDbContext db, int? vehicleId)
    {
        decimal? vehicleRent = null, vehicleDeposit = null;

        if (vehicleId.HasValue)
        {
            var v = await db.Vehicles.AsNoTracking()
                .Where(x => x.VehicleId == vehicleId.Value)
                .Select(x => new { x.DailyRent, x.Deposit })
                .FirstOrDefaultAsync();
            if (v != null)
            {
                vehicleRent = v.DailyRent;
                vehicleDeposit = v.Deposit;
            }
        }

        // 车辆侧留空的两项才去读全局配置，能少查一次是一次
        decimal globalRent = FallbackDailyRent, globalDeposit = FallbackDeposit;
        if (vehicleRent == null || vehicleDeposit == null)
        {
            var cfgs = await db.SystemConfigs
                .Where(c => c.ConfigKey == "daily_rent" || c.ConfigKey == "deposit")
                .ToListAsync();
            globalRent = ConfigValue.Decimal(cfgs, "daily_rent", FallbackDailyRent);
            globalDeposit = ConfigValue.Decimal(cfgs, "deposit", FallbackDeposit);
        }

        return (vehicleRent ?? globalRent, vehicleDeposit ?? globalDeposit);
    }

    /// <summary>解析某辆车的计价，并顺带给出小时价（四处调用点都直接用这个，别各自再除一次 24）</summary>
    public static async Task<(decimal DailyRent, decimal HourlyRent, decimal Deposit)> ResolveHourlyAsync(
        AppDbContext db, int? vehicleId)
    {
        var (dailyRent, deposit) = await ResolveAsync(db, vehicleId);
        return (dailyRent, HourlyRent(dailyRent), deposit);
    }
}

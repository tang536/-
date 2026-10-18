using ERental.Models;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>
/// 会员折扣关联读取（3NF 支持）。
/// customer.member_discount 冗余列已删除：customer → member_level → rights.discount
/// 是传递依赖，折扣唯一真源收敛在 member_rights，读取时按等级名关联。
/// 等级不在权益表中（如出厂未配置、普通会员）时按不打折 1.00 处理，业务不中断。
/// </summary>
public static class MemberDiscountLookup
{
    /// <summary>取某等级的折扣系数（越小越优惠）。</summary>
    public static async Task<decimal> OfLevelAsync(Data.AppDbContext db, string? level)
    {
        if (string.IsNullOrWhiteSpace(level)) return 1.00m;
        return await db.MemberRights.AsNoTracking()
            .Where(m => m.MemberLevel == level)
            .Select(m => (decimal?)m.Discount)
            .FirstOrDefaultAsync() ?? 1.00m;
    }

    /// <summary>批量填充客户档案的展示折扣（列表/详情接口出参用，前端字段名不变）。</summary>
    public static async Task FillAsync(Data.AppDbContext db, IEnumerable<Customer> customers)
    {
        var list = customers.ToList();
        if (list.Count == 0) return;
        var levels = list.Select(c => c.MemberLevel).Distinct().ToList();
        var map = await db.MemberRights.AsNoTracking()
            .Where(m => levels.Contains(m.MemberLevel))
            .ToDictionaryAsync(m => m.MemberLevel, m => m.Discount);
        foreach (var c in list)
            c.MemberDiscount = map.GetValueOrDefault(c.MemberLevel, 1.00m);
    }
}

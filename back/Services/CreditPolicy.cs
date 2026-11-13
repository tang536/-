using ERental.Data;
using Microsoft.EntityFrameworkCore;

namespace ERental.Services;

/// <summary>
/// 租前信用体检的结论。
/// 三个数字是「事实」，后面三项是「据此得出的处置」，前端只负责显示，不再二次判断。
/// </summary>
public sealed record CreditVerdict(
    int OverdueCount,
    decimal DamageTotal,
    int UnpaidSupplementCount,
    decimal DepositMultiplier,
    int MaxLeaseDays,
    string? BlockReason,
    string? Note)
{
    /// <summary>按倍数调整押金（1 倍即原价，不做无谓的乘法误差）</summary>
    public decimal ApplyToDeposit(decimal deposit) =>
        DepositMultiplier == 1m ? deposit : Math.Round(deposit * DepositMultiplier, 2);

    /// <summary>没有任何限制的默认结论（散客、首次租车、或查不到记录时都用它）</summary>
    public static CreditVerdict Clean => new(0, 0m, 0, 1m, 0, null, null);
}

/// <summary>
/// 客户信用 → 租车门槛。
///
/// 为什么要有这一层：信用标签原来只是「客户管理」页上的一列颜色，
/// 前台看到了也照样免押开单，等于给了一个谁都不看的参考。
/// 真正能让门店少亏钱的只有两件事：**钱没结清的人先不给租**、**老赖多收押金 / 只许短租**。
///
/// 三类事实全部从结算视图现算，不落表（理由同 CustomerService.FillCreditAsync：
/// 落表就要维护双份口径，顾客付清欠款后必须回来改标记，漏一次就永远拦着人）。
/// 规则本身可配：改倍数 / 阈值不需要动代码，在「系统配置」里改即可。
/// </summary>
public class CreditPolicy(AppDbContext db)
{
    /// <summary>逾期达阈值客户要交的押金倍数（默认 2 倍）</summary>
    private const string KeyMultiplier = "credit_deposit_multiplier";

    /// <summary>逾期次数阈值（默认 3 次；0 表示关闭这条规则）</summary>
    private const string KeyOverdueThreshold = "credit_overdue_threshold";

    /// <summary>受限客户单次租期上限（天，默认 7 天；0 表示不限）</summary>
    private const string KeyMaxLeaseDays = "credit_max_lease_days";

    /// <summary>累计损坏赔付到这个金额也按「高风险」处理（元）。用固定值而不是配置键，
    /// 是因为它跟着押金档次走（一个押金量级的赔付就该提高押金），配错了反而更危险。</summary>
    private const decimal DamageRiskLine = 300m;

    private readonly AppDbContext _db = db;

    /// <summary>评估某个客户的租车信用（客户不存在或没结算过，一律按「干净」处理）</summary>
    public async Task<CreditVerdict> EvaluateAsync(int customerId)
    {
        if (customerId <= 0) return CreditVerdict.Clean;

        var bills = await _db.SettlementBills.AsNoTracking()
            .Where(b => b.CustomerId == customerId)
            .Select(b => new { b.OverdueFee, b.DamageFee, b.SupplementAmount, b.SupplementCollected })
            .ToListAsync();

        //口径与「客户管理」页的信用标签完全一致：只算已结算单据
        var overdue = bills.Count(b => b.OverdueFee > 0);
        var damage = bills.Sum(b => b.DamageFee);
        var unpaid = bills.Count(b => b.SupplementAmount > 0 && b.SupplementCollected != 1);

        var keys = new[] { KeyMultiplier, KeyOverdueThreshold, KeyMaxLeaseDays };
        var cfgs = await _db.SystemConfigs.Where(c => keys.Contains(c.ConfigKey)).ToListAsync();
        var multiplier = ConfigValue.Decimal(cfgs, KeyMultiplier, 2m);
        var threshold = ConfigValue.Int(cfgs, KeyOverdueThreshold, 3);
        var maxDays = ConfigValue.Int(cfgs, KeyMaxLeaseDays, 7);

        //① 欠钱不给租：补缴没清的单据说明上一个租车周期还没结完账，
        //   再开新单等于把两笔账叠在一起，催收时连哪笔是哪笔都说不清。
        if (unpaid > 0)
        {
            return new CreditVerdict(overdue, damage, unpaid, 1m, 0,
                $"该客户还有 {unpaid} 笔补缴款未收清，请先结清后再开单", null);
        }

        //② 逾期多 / 损坏多：押金加倍 + 限租期。押金是门店唯一的对冲手段，
        //   逾期惯犯还按普通价收押金，等于用门店的钱补贴他的习惯。
        var riskyByOverdue = threshold > 0 && overdue >= threshold;
        var riskyByDamage = damage >= DamageRiskLine;
        if (riskyByOverdue || riskyByDamage)
        {
            var reasons = new List<string>();
            if (riskyByOverdue) reasons.Add($"历史逾期 {overdue} 次（阈值 {threshold} 次）");
            if (riskyByDamage) reasons.Add($"累计损坏赔付 ¥{damage:0.00}");

            var note = $"该客户 {string.Join("、", reasons)}：押金按 {multiplier:0.##} 倍收取";
            if (maxDays > 0) note += $"，单次租期不超过 {maxDays} 天";
            return new CreditVerdict(overdue, damage, unpaid, multiplier,
                maxDays > 0 ? maxDays : 0, null, note);
        }

        return new CreditVerdict(overdue, damage, unpaid, 1m, 0, null, null);
    }
}

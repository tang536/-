namespace ERental.Services;

/// <summary>
/// 结算金额的纯计算部分（不碰数据库，便于单元测试锁住口径）。
///
/// 业务规则（需求 4.2.5「归还结算、多退少补」）：
///   ① 租金合计 = 约定租期内租金 + 逾期费。逾期费本质是「超出约定还车时间那段的租金」，
///      单价上浮（默认 1.5 倍），所以它是租金的一部分，不单列为附加费；
///   ② 预付租金按实结算：多收的退、少收的补；
///   ③ 押金扣除损坏费后退回；
///   ④ 退款与补缴互斥：净额为正退给顾客，为负由顾客补缴，不会同时出现。
///
/// 所有中间结果都 <c>Math.Round(x, 2)</c>，与库里 decimal(10,2) 的精度一致，
/// 否则会出现「页面显示 ¥0.005、落库变 ¥0.01」的对不上账。
/// </summary>
public static class SettlementMath
{
    /// <summary>
    /// 结算净额。
    /// </summary>
    /// <param name="prepaidRent">下单时预付的租金（order.total_rent），可能为 0（到店付）</param>
    /// <param name="deposit">押金（order.deposit）</param>
    /// <param name="damageFee">损坏/赔偿费，由前台现场录入，试算传 0</param>
    /// <param name="actualRent">约定租期内的实际租金</param>
    /// <param name="overdueFee">逾期费（已按倍数算好）</param>
    /// <returns>租金合计、租金差额（正=应退、负=应补）、退款额、补缴额</returns>
    public static (decimal RentTotal, decimal RentDiff, decimal Refund, decimal Supplement) Settle(
        decimal prepaidRent, decimal deposit, decimal damageFee,
        decimal actualRent, decimal overdueFee)
    {
        var rentTotal = Math.Round(actualRent + overdueFee, 2);
        var rentDiff = Math.Round(prepaidRent - rentTotal, 2);
        var net = Math.Round(rentDiff + deposit - damageFee, 2);

        // 净额为负表示顾客还要补钱，此时退款额必须是 0（而不是负数），避免下游把负数当退款发起
        return (rentTotal, rentDiff, Math.Max(0m, net), net < 0 ? -net : 0m);
    }
}

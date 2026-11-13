namespace ERental.Services;

///<summary>退款请求号的统一构造规则</summary>
internal static class RefundKey
{
    ///<summary>按累计应退金额生成请求号</summary>
    public static string For(int payId, decimal cumulativeAmount)
    {
        //decimal.Round 用银行家舍入会让 .005 变成 .00
        var cents = (int)decimal.Round(cumulativeAmount * 100m, 0, MidpointRounding.AwayFromZero);
        return $"{payId}R{cents}";
    }
}

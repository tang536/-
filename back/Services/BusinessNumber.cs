using System.Globalization;
using System.Security.Cryptography;

namespace ERental.Services;

/// <summary>
/// 业务单号统一生成器：时间戳负责人工可读与对账，密码学随机段负责防碰撞与不可猜测。
///
/// 为什么必须集中：
///   原先 CheckService / MemberService / SupplementService 各自写
///   <c>new Random().Next(100, 999)</c>，只有 900 个取值。这些编号分别落在
///   uk_check_no、uk_report_no、uk_config_key（memberpay.* 支付意向）与支付通道的
///   out_trade_no 上，同一秒内并发就会撞唯一键；更要紧的是 HY 交易号是支付意向的
///   临时凭证，可枚举等于把别人的待支付意向暴露出去。
///
/// 注意 <c>RandomNumberGenerator.GetHexString(n)</c> 的参数是**字符数**，
/// 不是字节数：传 4 只得到 4 个十六进制字符（16 bit），默认传 8 得 32 bit。
/// </summary>
internal static class BusinessNumber
{
    /// <summary>默认随机段长度（十六进制字符数）</summary>
    public const int DefaultHexLength = 8;

    /// <summary>
    /// 前缀 + 时间戳 + 随机段。时间戳格式可换成 yyyyMMdd（按日编号的报表）；
    /// 传入 <paramref name="stamp"/> 时按业务日期编号，而不是按"生成的此刻"。
    /// </summary>
    public static string New(string prefix, int hexLength = DefaultHexLength, string stampFormat = "yyyyMMddHHmmss", DateTime? stamp = null)
    {
        if (string.IsNullOrWhiteSpace(prefix)) throw new BusinessException("单号前缀不能为空");
        if (hexLength is < 4 or > 32) throw new BusinessException("随机段长度需在 4 ~ 32 之间");

        return prefix
            + (stamp ?? DateTime.Now).ToString(stampFormat, CultureInfo.InvariantCulture)
            + RandomNumberGenerator.GetHexString(hexLength);
    }

    /// <summary>带中间业务键的复合单号（补缴单：SP-订单号-时间戳-随机段）</summary>
    public static string NewComposite(string prefix, string middle, string separator, int hexLength = DefaultHexLength, string stampFormat = "yyyyMMddHHmmssfff")
    {
        if (string.IsNullOrWhiteSpace(middle)) throw new BusinessException("复合单号缺少业务键");

        return prefix + separator + middle + separator
            + DateTime.Now.ToString(stampFormat, CultureInfo.InvariantCulture)
            + separator + RandomNumberGenerator.GetHexString(hexLength);
    }
}

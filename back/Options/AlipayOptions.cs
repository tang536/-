namespace ERental.Options;

/// <summary>
/// 支付宝接入的**静态参数**（签名算法等非凭证项）。
/// ⚠️ 本类**不承载任何凭证**。历史上的 AppId / PrivateKey / AlipayPublicKey / NotifyUrl /
/// ReturnUrl / Gateway / Enabled 字段已全部移除，原因：
///   ① 这些值早改由 <see cref="Services.PayConfigService"/> 按「生产→沙箱→模拟」三级降级
///      从 system_config 动态解析，本类的同名字段只是"看着像配置源"的陷阱；
///   ② 其中含密钥，放在配置类里极易被顺手写进日志或序列化进响应。
/// 凭证取值链路见 <see cref="Services.SecretProvider"/>：环境变量 → 用户机密 → 数据库。
/// 密钥语义备忘（理解这两把钥匙是接入支付宝的前提）：
///  - 应用私钥：我方持有，用于**给请求签名**，证明"这个下单请求是我们发的"。绝不外泄。
///  - 支付宝公钥：平台持有，用于**验证回调签名**，证明"这条到账通知真是支付宝发的"。
///    不验签就等于任何人都能伪造一条"支付成功"把你的订单刷成已支付。
/// </summary>
public class AlipayOptions
{
    /// <summary>签名算法，RSA2 即 RSA-SHA256（支付宝当前推荐且默认）</summary>
    public string SignType { get; set; } = "RSA2";
}

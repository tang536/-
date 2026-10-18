using System.Security.Cryptography;
using System.Text;

namespace ERental.Services;

/// <summary>
/// 支付宝 RSA2 签名 / 验签工具（纯 .NET 内置加密库实现，无第三方依赖）。
///
/// 为什么手写而不用官方 SDK：官方 SDK 体量大、封装深，出问题时难以定位；
/// 而支付宝的签名规则本身只有三句话——
///   ① 待签串 = 把所有「非空」参数按 key 升序，用 k=v 拼起来（不 URL 编码）；
///   ② 用我方应用私钥做 SHA256withRSA，再 Base64，得到 sign 参数；
///   ③ 回调验签时，把除 sign / sign_type 外的参数同样拼成待签串，
///      用「支付宝公钥」验签 —— 通过才认为这条通知真实可信。
/// 手写出来不到百行，每一步都看得见，答辩时能讲清楚。
///
/// ⚠️ **请求签名与回调验签的过滤规则不同，这是最容易踩的坑**（本项目真实踩过）：
///   - **请求签名**：只有 `sign` 本身不参与，**`sign_type` 必须参与**。
///     漏掉 sign_type 会让网关按「含 sign_type」的口径复现待签串，与我方签的对不上，
///     报 `isv.invalid-signature`，且现象与「密钥不配对」一模一样，极难分辨。
///   - **回调验签**：`sign` 与 `sign_type` 都**不**参与。
///     详见 <see cref="BuildSignContentForVerify"/>。
/// </summary>
public static class AlipaySigner
{
    /// <summary>
    /// 生成**请求**待签名串：过滤空值参数与 `sign` 本身，按 key 字典序升序，用 &amp; 连接 k=v。
    /// <c>sign_type</c> 参与签名（支付宝规范要求）；
    /// 注意：这里**不做 URL 编码**——URL 编码只发生在最终把参数拼进请求时。
    /// </summary>
    public static string BuildSignContent(IDictionary<string, string?> parameters)
    {
        var filtered = parameters
            .Where(kv => kv.Key != "sign"
                         && !string.IsNullOrEmpty(kv.Value))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}");

        return string.Join("&", filtered);
    }

    /// <summary>
    /// 生成**回调验签**用待签串：`sign` 与 `sign_type` 均不参与（与请求签名口径不同，勿复用）。
    /// </summary>
    public static string BuildSignContentForVerify(IDictionary<string, string?> parameters)
    {
        var filtered = parameters
            .Where(kv => kv.Key != "sign" && kv.Key != "sign_type"
                         && !string.IsNullOrEmpty(kv.Value))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}");

        return string.Join("&", filtered);
    }

    /// <summary>用应用私钥对内容做 SHA256withRSA 签名，返回 Base64 字符串</summary>
    public static string Sign(string content, string privateKey)
    {
        using var rsa = CreateRsaFromPrivateKey(privateKey);
        var data = Encoding.UTF8.GetBytes(content);
        var signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(signature);
    }

    /// <summary>用支付宝公钥验证签名（回调验签）。任何异常都返回 false，绝不因异常放行</summary>
    public static bool Verify(string content, string signature, string alipayPublicKey)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;
        try
        {
            using var rsa = CreateRsaFromPublicKey(alipayPublicKey);
            var data = Encoding.UTF8.GetBytes(content);
            var sig = Convert.FromBase64String(signature);
            return rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            // 验签失败绝不能让请求 500 或放行：返回 false，由调用方按「验签不通过」处理
            return false;
        }
    }

    /// <summary>
    /// 把私钥字符串转成 RSA 对象。
    ///
    /// 私钥有两种常见格式，必须都支持（不同工具导出格式不同，混用是踩坑重灾区）：
    ///  - **PKCS#8**（`-----BEGIN PRIVATE KEY-----`）：支付宝开放平台"密钥工具"默认导出，**推荐**；
    ///  - **PKCS#1**（`-----BEGIN RSA PRIVATE KEY-----`）：openssl genrsa 默认、部分第三方生成器导出。
    /// 两者 DER 结构不同：PKCS#8 外层是 PrivateKeyInfo，PKCS#1 直接是 RSAPrivateKey。
    /// 这里先按 PKCS#8 试，失败再按 PKCS#1 试，任一种成功即可，调用方无需关心格式。
    /// </summary>
    // public：微信支付 APIv3 的请求签名 / 回调验签同样是「SHA256withRSA + PEM 文本」，
    // WechatSigner 直接复用这里的密钥装载逻辑，避免第二份 NormalizeKey 各写各的。
    private static RSA CreateRsaFromPrivateKey(string privateKey)
    {
        var raw = Convert.FromBase64String(NormalizeKey(privateKey));
        var rsa = RSA.Create();
        try
        {
            rsa.ImportPkcs8PrivateKey(raw, out _);
        }
        catch (CryptographicException)
        {
            // 不是 PKCS#8，回退按 PKCS#1（RSAPrivateKey）解析
            rsa.ImportRSAPrivateKey(raw, out _);
        }
        return rsa;
    }

    /// <summary>把公钥字符串转成 RSA 对象（X.509 SubjectPublicKeyInfo 格式；支付宝公钥 / 微信平台公钥通用）</summary>
    private static RSA CreateRsaFromPublicKey(string publicKey)
    {
        var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(NormalizeKey(publicKey)), out _);
        return rsa;
    }

    /// <summary>
    /// 由应用私钥推导出它对应的「应用公钥」（X.509 SubjectPublicKeyInfo 的 Base64 单行），
    /// 即应该上传到支付宝开放平台「接口加签方式」的那串值。
    ///
    /// 用途：沙箱报 isv.invalid-signature 时，用它核对「后台登记的公钥」与「本地私钥」是否配对——
    /// 两者不一致是沙箱接入最常见的翻车点。
    /// </summary>
    public static string DeriveApplicationPublicKey(string privateKey)
    {
        using var rsa = CreateRsaFromPrivateKey(privateKey);
        return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>
    /// 计算公钥的「模数指纹」：取 RSA 模数字节的 SHA256 前 8 字节，转十六进制。
    ///
    /// 为什么需要它：应用公钥是 392 字符的长串，肉眼比对两把公钥几乎不可能；
    /// 而 RSA 的公开指数在本平台固定为 65537，模数相同即代表同一把密钥。
    /// 于是把 392 字符压缩成 16 个十六进制字符，用户可直接与沙箱后台的指纹对照。
    ///
    /// ⚠️ 必须从 **DER 编码**里取模数，不能用 <c>ExportParameters().Modulus</c>：
    /// 后者是 .NET 的「大端无符号整数」，最高位为 1 时会**丢掉 DER 用来标识正数的前导 0x00**，
    /// 导致结果比 DER 少一个字节，SHA256 完全不同（曾因此得出错误指纹 f94e989b，正确应为 0e380a74）。
    /// 这里统一口径：导出 SubjectPublicKeyInfo → 解析出 BIT STRING 内的 RSAPublicKey → 取第一个 INTEGER。
    /// </summary>
    public static string PublicKeyFingerprint(string publicKey)
    {
        using var rsa = CreateRsaFromPublicKey(publicKey);
        var spki = rsa.ExportSubjectPublicKeyInfo();
        var modulus = ReadSpkiModulus(spki);
        var hash = SHA256.HashData(modulus);
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>从 SubjectPublicKeyInfo DER 中解析出 RSA 模数字节（含 DER 的正数前导 0x00）</summary>
    private static byte[] ReadSpkiModulus(byte[] spki)
    {
        // SPKI ::= SEQUENCE { AlgorithmIdentifier SEQUENCE, subjectPublicKey BIT STRING }
        var (_, spkiStart, spkiEnd, _) = ReadTlv(spki, 0);

        // 定位 BIT STRING，拿到它内容的起始下标
        var i = spkiStart;
        var bitContentStart = -1;
        while (i < spkiEnd)
        {
            var (tag, cs, _, next) = ReadTlv(spki, i);
            if (tag == 0x03) { bitContentStart = cs; break; }
            i = next;
        }
        if (bitContentStart < 0) throw new CryptographicException("SPKI 中未找到 BIT STRING");

        // BIT STRING 内容首字节是「未使用位数」（通常 0x00），其后才是 RSAPublicKey 的 DER
        var rsapkPos = bitContentStart + 1;
        var (_, rsaStart, _, _) = ReadTlv(spki, rsapkPos);

        // RSAPublicKey ::= SEQUENCE { modulus INTEGER, publicExponent INTEGER }
        var (mTag, mStart, mEnd, _) = ReadTlv(spki, rsaStart);
        return mTag != 0x02
            ? throw new CryptographicException("RSAPublicKey 首个元素不是 INTEGER")
            : spki[mStart..mEnd];
    }

    /// <summary>读一个 DER TLV，返回 (tag, 内容起, 内容止, 下一项起)</summary>
    private static (byte Tag, int ContentStart, int ContentEnd, int Next) ReadTlv(byte[] b, int pos)
    {
        var tag = b[pos];
        int len = b[pos + 1];
        var lenBytes = 1;
        if (len >= 0x80)
        {
            var n = len & 0x7F;
            len = 0;
            for (var k = 0; k < n; k++) len = (len << 8) | b[pos + 2 + k];
            lenBytes = 1 + n;
        }
        var contentStart = pos + 1 + lenBytes;
        return (tag, contentStart, contentStart + len, contentStart + len);
    }

    /// <summary>判断私钥文本是 PKCS#1 还是 PKCS#8，用于提示用户粘贴的格式</summary>
    public static string DetectPrivateKeyFormat(string privateKey)
    {
        var body = NormalizeKey(privateKey);
        if (string.IsNullOrEmpty(body)) return "空";
        // MIIEow / MIIEpA 等是 PKCS#1（RSA PRIVATE KEY）；MIIEvQ 等是 PKCS#8（PRIVATE KEY）
        if (body.StartsWith("MIIEow", StringComparison.Ordinal)) return "PKCS#1 (RSA PRIVATE KEY)";
        if (body.StartsWith("MIIEvQ", StringComparison.Ordinal)) return "PKCS#8 (PRIVATE KEY)";
        // 长度也可辅助判断：2048 位 PKCS#1 约 1588 字符，PKCS#8 约 1624 字符
        return body.Length > 1600 ? "PKCS#8 (PRIVATE KEY)" : "PKCS#1 (RSA PRIVATE KEY)";
    }

    /// <summary>
    /// 清理密钥文本：去掉 PEM 头尾标记与所有空白。
    /// 用户从开放平台复制密钥时经常把「-----BEGIN PUBLIC KEY-----」一起粘进来，
    /// 若不去掉，Convert.FromBase64String 会直接抛异常。
    /// </summary>
    private static string NormalizeKey(string key)
    {
        return key
            .Replace("-----BEGIN PRIVATE KEY-----", "")
            .Replace("-----END PRIVATE KEY-----", "")
            .Replace("-----BEGIN RSA PRIVATE KEY-----", "")
            .Replace("-----END RSA PRIVATE KEY-----", "")
            .Replace("-----BEGIN PUBLIC KEY-----", "")
            .Replace("-----END PUBLIC KEY-----", "")
            .Replace("\r", "")
            .Replace("\n", "")
            .Replace(" ", "")
            .Trim();
    }
}

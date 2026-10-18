using System.Text.RegularExpressions;

namespace ERental.Services;

/// <summary>
/// 业务字段格式校验的唯一入口。
///
/// 采用 .NET 源生成正则（<see cref="GeneratedRegexAttribute"/>）：正则状态机在**编译期**生成，
/// 运行期不再解析表达式、不再查正则缓存，且同一规则在多处引用时不会再出现写法漂移
/// （此前身份证 / 手机号 / 用户名三条规则在 Auth、Customer、Order 三个服务里各写了一遍）。
/// </summary>
public static partial class Validators
{
    /// <summary>身份证号：18 位数字，末位可为 X / x</summary>
    [GeneratedRegex(@"^\d{17}[\dXx]$")]
    private static partial Regex IdCardPattern();

    /// <summary>手机号：11 位国内号码（1 开头，第二位 3-9）</summary>
    [GeneratedRegex(@"^1[3-9]\d{9}$")]
    private static partial Regex PhonePattern();

    /// <summary>登录用户名：3-20 位字母 / 数字 / 下划线 / 中文</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\u4e00-\u9fa5]{3,20}$")]
    private static partial Regex LoginAccountPattern();

    /// <summary>身份证号是否为 18 位合法格式</summary>
    public static bool IsIdCard(string? value) =>
        !string.IsNullOrEmpty(value) && IdCardPattern().IsMatch(value);

    /// <summary>是否为空或合法手机号（可空字段留空视为通过）</summary>
    public static bool IsPhoneOrEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) || PhonePattern().IsMatch(value);

    /// <summary>手机号是否为 11 位合法格式</summary>
    public static bool IsPhone(string? value) =>
        !string.IsNullOrEmpty(value) && PhonePattern().IsMatch(value);

    /// <summary>用户名是否为 3-20 位字母 / 数字 / 下划线 / 中文</summary>
    public static bool IsLoginAccount(string? value) =>
        !string.IsNullOrEmpty(value) && LoginAccountPattern().IsMatch(value);

    /// <summary>
    /// 密码复杂度：至少 8 位，且必须同时包含字母与数字。
    /// 需求文档 4.2.1 写的「复杂度校验」此前一直没有实现（注册只查长度 ≥6），
    /// 这里作为**统一规则**落在唯一校验入口：注册（顾客）、建号/重置（工作人员）三处共用。
    /// 注意**不**要求定期修改——那种策略在课设/门店场景只会逼员工把密码贴在显示器上。
    /// </summary>
    public static bool IsStrongPassword(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length >= 8
        && value.Any(char.IsLetter) && value.Any(char.IsDigit);

    /// <summary>密码复杂度的提示文案（多处共用，避免三处各写一句对不上的话）</summary>
    public const string PasswordRuleHint = "密码至少 8 位，且需同时包含字母和数字";
}

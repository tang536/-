namespace ERental.Options;

/// <summary>
/// 局域网接入开关。
/// 课设演示需要「手机 / 另一台电脑连同一个 Wi-Fi 就能访问系统」，但回调类地址
/// （支付宝 notify_url / return_url）一旦写死 localhost，换台设备访问时就失效——
/// 支付平台的服务器会去回调 localhost，那当然打不通。
/// 这里不改动管理员在「支付配置」页填写的值，只是在其**为空**时按本机局域网 IP
/// 兜底生成一个可用地址，让"局域网演示"开箱即用；管理员手工填过的值优先级更高，
/// 绝不会被覆盖（生产环境要靠内网穿透或公网域名，必须由人显式指定）。
/// </summary>
public class LanOptions
{
    /// <summary>是否启用局域网模式。由启动脚本注入环境变量 Lan__Enabled=true 打开。</summary>
    public bool Enabled { get; set; }

    /// <summary>本机对外 IP。留空时由程序自动探测（取第一个非回环 IPv4）。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// 前端端口，支付返回页 /pay-result 挂在它上面。
    /// 员工端与顾客端是同一个前端工程、同端口，故默认 5173；
    /// 字段名保留 CustomerPort 以兼容既有配置文件与启动脚本。
    /// 若填成 5174 等其它值，局域网模式下支付宝的 return_url 会指向不存在的端口，付款后回跳打不开。
    /// </summary>
    public int CustomerPort { get; set; } = 5173;

    /// <summary>
    /// 前端入口协议。局域网模式启动脚本会同时给 vite 注入 LAN_HOST=1，vite 据此启用
    /// 自签 HTTPS（登录密码与 JWT 不在公共 Wi-Fi 里裸奔），所以这里默认必须是 https。
    /// 一旦与前端实际协议不符，支付回跳就会变成「明文 HTTP 打到 TLS 端口」——
    /// 服务端在握手阶段直接断开，手机浏览器只显示「xx 未发送任何数据」
    /// （Chrome 的 ERR_EMPTY_RESPONSE），而不是常见的「无法访问此网站」。
    /// </summary>
    public string FrontScheme { get; set; } = "https";
}

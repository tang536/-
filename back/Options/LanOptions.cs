namespace ERental.Options;

/// <summary>
/// 局域网接入开关。
///
/// 课设演示需要「手机 / 另一台电脑连同一个 Wi-Fi 就能访问系统」，但回调类地址
/// （支付宝 notify_url / return_url）一旦写死 localhost，换台设备访问时就失效——
/// 支付平台的服务器会去回调 localhost，那当然打不通。
///
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
    /// 字段名沿用 CustomerPort 是历史原因：早期「员工端 5173 / 顾客端 5174」是两个独立 Vite 工程，
    /// 现已合并为单一前端工程、两端同端口，所以默认值取 5173。
    /// 留成 5174 会让局域网模式下支付宝的 return_url 指向一个不存在的端口，付款后回跳打不开。
    /// </summary>
    public int CustomerPort { get; set; } = 5173;
}

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ERental.Services;

/// <summary>
/// 局域网地址探测：把「本机在局域网里的 IP」找出来，供启动脚本与回调地址兜底使用。
///
/// 为什么不用 Dns.GetHostAddresses(Dns.GetHostName()) —— 依赖主机名解析，
/// 在多网卡（虚拟网卡 / VPN / WSL / Docker）机器上会返回一堆无用地址，
/// 且顺序不保证。这里改为遍历真实在用的网卡，按「无线 > 有线」优先级挑第一个
/// 已启用、非回环、非链路本地的 IPv4，更贴近"用户实际从哪台设备访问我"。
/// </summary>
public static class NetworkHelper
{
    /// <summary>取本机最佳局域网 IPv4；找不到时返回 null。</summary>
    public static string? ResolveLanIp()
    {
        var candidates = new List<(int Priority, string Ip)>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            // 跳过未启用 / 回环 / 隧道类（VPN、Teredo 等，它们的地址跨不了局域网）
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                or NetworkInterfaceType.Tunnel) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = addr.Address.ToString();
                if (IPAddress.IsLoopback(addr.Address)) continue;
                // 169.254.x.x 是 DHCP 失败时的自动私有地址，连不通别人
                if (ip.StartsWith("169.254.")) continue;

                // 无线优先：演示场景多是笔记本连 Wi-Fi，手机扫码后要连同一个网段
                var priority = nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 ? 0 : 1;
                candidates.Add((priority, ip));
            }
        }

        return candidates.OrderBy(c => c.Priority).Select(c => c.Ip).FirstOrDefault();
    }
}

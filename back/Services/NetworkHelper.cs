using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ERental.Services;

///<summary>局域网地址探测：把「本机在局域网里的 IP」找出来，供启动脚本与回调地址兜底使用</summary>
internal static class NetworkHelper
{
    ///<summary>取本机最佳局域网 IPv4</summary>
    public static string? ResolveLanIp()
    {
        var candidates=new List<(int Priority,string Ip)>();

        foreach(var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            //网卡级筛选独立成 IsCandidateInterface：判的是「这块网卡值不值得看」
            if(!IsCandidateInterface(nic)) continue;
            CollectCandidateAddresses(nic,candidates);
        }

        //排序语义不变
        return candidates.OrderBy(c=>c.Priority).Select(c=>c.Ip).FirstOrDefault();
    }

    ///<summary>只保留已启用且非回环/非隧道的网卡（VPN、Teredo 的地址跨不了局域网）</summary>
    private static bool IsCandidateInterface(NetworkInterface nic) =>
        nic.OperationalStatus==OperationalStatus.Up
        && nic.NetworkInterfaceType is not(NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel);

    ///<summary>收集单块网卡上可用的 IPv4 地址，附带「无线优先」的排序权重</summary>
    private static void CollectCandidateAddresses(NetworkInterface nic,List<(int Priority,string Ip)> candidates)
    {
        //循环体只用到地址本身，先 Select 出地址再过滤，省掉每轮一次 .Address 取值
        foreach(var address in nic.GetIPProperties().UnicastAddresses
                     .Select(a=>a.Address)
                     .Where(a=>a.AddressFamily==AddressFamily.InterNetwork))
        {
            if(!IsLanAddress(address)) continue;

            //无线优先：演示场景多是笔记本连 Wi-Fi，手机扫码后要连同一个网段
            var priority=nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211?0:1;
            candidates.Add((priority,address.ToString()));
        }
    }

    ///<summary>该地址能否作为局域网地址：排除回环与 DHCP 失败时的 169.254 自动私有地址</summary>
    private static bool IsLanAddress(IPAddress address)
    {
        if(IPAddress.IsLoopback(address)) return false;
        //169.254.x.x 连不通别人（原判据按字面前缀比较，这里补上 Ordinal，结果不变）
        return !address.ToString().StartsWith("169.254.",StringComparison.Ordinal);
    }
}

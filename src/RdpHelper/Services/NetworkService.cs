using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RdpHelper.Services;

public sealed record LanAddress(string Adapter, string Description, string Ip, bool HasGateway, bool IsVirtual, string Kind)
{
    public string Badge => IsVirtual ? L.T("虚拟网卡") : HasGateway ? L.T("推荐") : "";
    public bool ShowBadge => Badge.Length > 0;
}

public static class NetworkService
{
    private static readonly string[] VirtualHints =
    {
        "vmware", "virtualbox", "hyper-v virtual", "vethernet", "wsl", "docker", "loopback", "tap-", "wintun",
        "tailscale", "zerotier", "clash", "meta", "pseudo", "bluetooth", "npcap", "teredo", "isatap",
    };

    public static List<LanAddress> GetLanAddresses()
    {
        var list = new List<LanAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); } catch { continue; }
            bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            string text = (ni.Name + " " + ni.Description).ToLowerInvariant();
            bool isVirtual = VirtualHints.Any(text.Contains);
            string kind = ni.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => L.T("以太网"),
                _ => ni.NetworkInterfaceType.ToString(),
            };
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = ua.Address.ToString();
                if (ip.StartsWith("169.254.") || ip.StartsWith("127.")) continue;
                list.Add(new LanAddress(ni.Name, ni.Description, ip, gw, isVirtual, kind));
            }
        }
        return list.OrderByDescending(a => a.HasGateway && !a.IsVirtual)
                   .ThenBy(a => a.IsVirtual)
                   .ThenBy(a => a.Adapter)
                   .ToList();
    }

    public static LanAddress? Primary(IEnumerable<LanAddress> list) => list.FirstOrDefault();
}

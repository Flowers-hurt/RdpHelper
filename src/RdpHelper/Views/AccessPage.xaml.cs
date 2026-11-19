using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using RdpHelper.Services;

namespace RdpHelper.Views;

public partial class AccessPage : Page
{
    public AccessPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadPolicies();
    }

    private void LoadPolicies()
    {
        var (ra, rd) = FirewallService.GetPolicy(RemoteKind.Rdp);
        var (sa, sd) = FirewallService.GetPolicy(RemoteKind.Ssh);
        RdpAllowBox.Text = string.Join(Environment.NewLine, ra);
        RdpDenyBox.Text = string.Join(Environment.NewLine, rd);
        SshAllowBox.Text = string.Join(Environment.NewLine, sa);
        SshDenyBox.Text = string.Join(Environment.NewLine, sd);
        RdpSummary.Text = Summary(ra, rd, RdpService.GetPort());
        SshSummary.Text = Summary(sa, sd, SshService.GetPort());
    }

    private static string Summary(List<string> allow, List<string> deny, int port) =>
        $"端口 {port} · " + (allow.Count == 0 ? "允许所有地址" : $"仅允许 {allow.Count} 条") + (deny.Count > 0 ? $" · 拦截 {deny.Count} 条" : " · 无黑名单");

    private static List<string> Lines(string text) =>
        FirewallService.Normalize(text.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries));

    private async Task SaveAsync(RemoteKind kind, string allowText, string denyText)
    {
        var allow = Lines(allowText);
        var deny = Lines(denyText);
        foreach (var item in allow.Concat(deny))
        {
            if (!FirewallService.IsValidEntry(item, out var err))
            {
                Notify.Warn("名单格式有误", err);
                return;
            }
        }
        if (kind == RemoteKind.Rdp && allow.Count > 0 && RdpService.IsInRemoteSession &&
            !RemotePage.Confirm("你正在远程使用这台电脑。启用白名单后，若你当前所在的 IP 不在名单内，保存后将无法再连回来。确定继续吗？"))
            return;

        Busy.Visibility = Visibility.Visible;
        IsEnabled = false;
        try
        {
            FirewallService.SavePolicy(kind, allow, deny);
            bool enabled;
            int port;
            if (kind == RemoteKind.Rdp) { enabled = RdpService.IsEnabled(); port = RdpService.GetPort(); }
            else { enabled = await SshService.IsRunningAsync(); port = SshService.GetPort(); }

            if (enabled)
            {
                var warn = await FirewallService.ApplyAsync(kind, port, true);
                RemotePage.Done("名单已保存并生效", warn);
            }
            else
            {
                Notify.Info("名单已保存", (kind == RemoteKind.Rdp ? "远程桌面" : "SSH") + " 当前未开启，开启时自动生效");
            }
        }
        catch (Exception ex) { Notify.Error("保存失败", ex.Message); }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            IsEnabled = true;
            LoadPolicies();
        }
    }

    /// <summary>根据首选网卡的 IP 和掩码算出网段，如 192.168.1.0/24。</summary>
    private static string? LocalSubnet()
    {
        var primary = NetworkService.Primary(NetworkService.GetLanAddresses());
        if (primary == null) return null;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.Address.ToString() != primary.Ip) continue;
                int bits = ua.PrefixLength;
                var ip = ua.Address.GetAddressBytes();
                uint v = (uint)(ip[0] << 24 | ip[1] << 16 | ip[2] << 8 | ip[3]);
                uint mask = bits == 0 ? 0 : uint.MaxValue << (32 - bits);
                uint net = v & mask;
                return $"{net >> 24}.{(net >> 16) & 255}.{(net >> 8) & 255}.{net & 255}/{bits}";
            }
        }
        return null;
    }

    private static void AppendLine(System.Windows.Controls.TextBox box, string? line)
    {
        if (line == null) { Notify.Warn("没找到局域网网段"); return; }
        if (Lines(box.Text).Contains(line)) return;
        box.Text = (box.Text.TrimEnd() + Environment.NewLine + line).Trim();
    }

    private async void SaveRdp_Click(object sender, RoutedEventArgs e) => await SaveAsync(RemoteKind.Rdp, RdpAllowBox.Text, RdpDenyBox.Text);
    private async void SaveSsh_Click(object sender, RoutedEventArgs e) => await SaveAsync(RemoteKind.Ssh, SshAllowBox.Text, SshDenyBox.Text);
    private void AddSubnetRdp_Click(object sender, RoutedEventArgs e) => AppendLine(RdpAllowBox, LocalSubnet());
    private void AddSubnetSsh_Click(object sender, RoutedEventArgs e) => AppendLine(SshAllowBox, LocalSubnet());
    private void ClearRdp_Click(object sender, RoutedEventArgs e) { RdpAllowBox.Text = ""; RdpDenyBox.Text = ""; }
    private void ClearSsh_Click(object sender, RoutedEventArgs e) { SshAllowBox.Text = ""; SshDenyBox.Text = ""; }
}

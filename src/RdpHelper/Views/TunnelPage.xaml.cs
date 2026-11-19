using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using RdpHelper.Services;
using RdpHelper.Services.Tunnels;
using Wpf.Ui.Controls;

namespace RdpHelper.Views;

public partial class TunnelPage : Page
{
    public TunnelPage()
    {
        InitializeComponent();
        CardsPanel.Children.Add(new TunnelCard(TunnelManager.Cloudflare, SymbolRegular.Cloud24));
        CardsPanel.Children.Add(new TunnelCard(TunnelManager.Ngrok, SymbolRegular.Globe24));
        CardsPanel.Children.Add(new TunnelCard(TunnelManager.Bore, SymbolRegular.PlugConnected24));
        Loaded += (_, _) => VisitState.Text = TunnelManager.Cloudflare.IsAccessRunning ? "转发运行中" : "";
    }

    private async void Visit_Click(object sender, RoutedEventArgs e)
    {
        var cf = TunnelManager.Cloudflare;
        var host = CloudflareProvider.NormalizeHost(VisitHostBox.Text);
        if (host.Length == 0) { Notify.Warn("请先粘贴对方的地址"); return; }
        if (!cf.IsInstalled)
        {
            Notify.Info("需要 cloudflared", "正在下载 Cloudflare 组件…");
            try { await cf.InstallAsync(null); }
            catch (Exception ex) { Notify.Error("下载 cloudflared 失败", ex.Message); return; }
        }
        VisitButton.IsEnabled = false;
        VisitState.Text = "正在建立转发…";
        try
        {
            int port = await cf.StartAccessAsync(host);
            VisitState.Text = $"转发运行中 → localhost:{port}";
            Process.Start(new ProcessStartInfo("mstsc.exe", $"/v:localhost:{port}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            VisitState.Text = "";
            Notify.Error("连接失败", ex.Message);
        }
        finally { VisitButton.IsEnabled = true; }
    }

    private void VisitStop_Click(object sender, RoutedEventArgs e)
    {
        TunnelManager.Cloudflare.StopAccess();
        VisitState.Text = "已断开";
    }

    private void OpenTools_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Tools);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Tools}\"") { UseShellExecute = true });
    }
}

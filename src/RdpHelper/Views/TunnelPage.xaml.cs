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
        // 默认只展示 bore（免注册、组件仅约 1 MB、对方直接用 mstsc），Cloudflare / ngrok 收在「更多服务商」里
        var bore = new TunnelCard(TunnelManager.Bore, SymbolRegular.PlugConnected24, recommended: true);
        var cloudflare = new TunnelCard(TunnelManager.Cloudflare, SymbolRegular.Cloud24);
        CardsPanel.Children.Add(bore);
        MoreCardsPanel.Children.Add(cloudflare);
        // bore.pub 被网络拦截时一键改用 Cloudflare（走 443 端口，一般网络都放行）
        bore.FallbackRequested += async () =>
        {
            MoreExpander.IsExpanded = true;
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            await cloudflare.StartFromFallbackAsync(bore.TargetIndex);
        };
        MoreCardsPanel.Children.Add(new TunnelCard(TunnelManager.Ngrok, SymbolRegular.Globe24));
        // 用过（已下载 / 正在运行）进阶服务商时自动展开，免得找不到
        MoreExpander.IsExpanded = TunnelManager.Cloudflare.IsInstalled || TunnelManager.Ngrok.IsInstalled
                                  || TunnelManager.Cloudflare.IsRunning || TunnelManager.Ngrok.IsRunning
                                  || TunnelManager.Cloudflare.IsAccessRunning;
        Loaded += (_, _) => VisitState.Text = TunnelManager.Cloudflare.IsAccessRunning ? L.T("转发运行中") : "";
    }

    private async void Visit_Click(object sender, RoutedEventArgs e)
    {
        var cf = TunnelManager.Cloudflare;
        var host = CloudflareProvider.NormalizeHost(VisitHostBox.Text);
        if (host.Length == 0) { Notify.Warn(L.T("请先粘贴对方的地址")); return; }
        if (!cf.IsInstalled)
        {
            Notify.Info(L.T("需要 cloudflared"), L.T("正在下载 Cloudflare 组件…"));
            try { await cf.InstallAsync(null); }
            catch (Exception ex) { Notify.Error(L.T("下载 cloudflared 失败"), ex.Message); return; }
        }
        VisitButton.IsEnabled = false;
        VisitState.Text = L.T("正在建立转发…");
        try
        {
            int port = await cf.StartAccessAsync(host);
            VisitState.Text = L.F("转发运行中 → localhost:{0}", port);
            Process.Start(new ProcessStartInfo("mstsc.exe", $"/v:localhost:{port}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            VisitState.Text = "";
            Notify.Error(L.T("连接失败"), ex.Message);
        }
        finally { VisitButton.IsEnabled = true; }
    }

    private void VisitStop_Click(object sender, RoutedEventArgs e)
    {
        TunnelManager.Cloudflare.StopAccess();
        VisitState.Text = L.T("已断开");
    }

    private void OpenTools_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Tools);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Tools}\"") { UseShellExecute = true });
    }
}

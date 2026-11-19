using System.Windows;
using System.Windows.Controls;
using RdpHelper.Services;
using Wpf.Ui.Controls;

namespace RdpHelper.Views;

public partial class SshPage : Page
{
    public SshPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        bool installed = SshService.IsInstalled;
        var (status, startType) = await SshService.GetStatusAsync();
        bool running = status == "Running";
        int port = SshService.GetPort();

        InstallCard.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        SshToggle.IsEnabled = installed;
        SshToggle.IsChecked = running;
        PortText.Text = port.ToString();
        if (string.IsNullOrEmpty(PortBox.Text)) PortBox.Text = port.ToString();
        ConfigPathText.Text = L.F("配置文件：{0}", SshService.ConfigPath);

        bool listening = running && RdpService.IsListening(port);
        if (!installed)
        {
            StatusDot.Fill = RemotePage.Gray;
            StatusText.Text = L.T("尚未安装 OpenSSH 服务器");
        }
        else
        {
            RemotePage.SetStatus(StatusDot, StatusText, running, listening,
                L.F("SSH 已开启 · 正在监听端口 {0} · 启动类型 {1}", port, startType),
                L.F("SSH 已开启 · 端口 {0} 暂未监听", port),
                L.F("SSH 已停止 · 启动类型 {0}", startType));
        }

        var lan = NetworkService.Primary(await Task.Run(NetworkService.GetLanAddresses));
        CommandText.Text = SshService.ConnectCommand(lan?.Ip ?? L.T("本机IP"), port);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        await Busy(InstallBusy, async () =>
        {
            var progress = new Progress<string>(s => InstallDesc.Text = s);
            await SshService.InstallAsync(progress);
            var warn = await SshService.EnableAsync(true);
            RemotePage.Done(L.T("OpenSSH 服务器已安装并启动"), warn);
        });
    }

    private async void SshToggle_Click(object sender, RoutedEventArgs e)
    {
        bool want = SshToggle.IsChecked == true;
        await Busy(SshBusy, async () =>
        {
            var warn = await SshService.EnableAsync(want);
            RemotePage.Done(want ? L.T("SSH 已开启") : L.T("SSH 已关闭"), warn);
        });
    }

    private async void ApplyPort_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port))
        {
            Notify.Warn(L.T("端口格式不对"), L.T("请输入 1024–65535 之间的数字"));
            return;
        }
        await ChangePort(port);
    }

    private void RandomPort_Click(object sender, RoutedEventArgs e) => PortBox.Text = RdpService.SuggestFreePort().ToString();

    private async void DefaultPort_Click(object sender, RoutedEventArgs e)
    {
        PortBox.Text = SshService.DefaultPort.ToString();
        await ChangePort(SshService.DefaultPort);
    }

    private async Task ChangePort(int port)
    {
        if (port == SshService.GetPort())
        {
            Notify.Info(L.T("端口没有变化"), L.F("当前已经是 {0}", port));
            return;
        }
        await Busy(PortBusy, async () =>
        {
            var warn = await SshService.SetPortAsync(port);
            RemotePage.Done(L.F("SSH 端口已改为 {0}", port), warn);
        });
    }

    private void CopyCmd_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteInfo.CopyToClipboard(CommandText.Text)) Notify.Success(L.T("已复制"), CommandText.Text);
    }

    private async Task Busy(ProgressRing ring, Func<Task> action)
    {
        ring.Visibility = Visibility.Visible;
        IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { Notify.Error(L.T("操作失败"), ex.Message); }
        finally
        {
            ring.Visibility = Visibility.Collapsed;
            IsEnabled = true;
            await RefreshAsync();
        }
    }
}

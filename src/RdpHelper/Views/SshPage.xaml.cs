using System.Windows;
using System.Windows.Controls;
using RdpHelper.Services;
using Wpf.Ui.Controls;

namespace RdpHelper.Views;

public partial class SshPage : Page
{
    // 安装流程是静态的：切换页面不会中断，也不会丢失进度
    private static Task? _installFlow;
    private static event Action? InstallFlowChanged;

    private readonly Action _onInstallerChanged;
    private readonly Action<string> _onInstallerLog;

    public SshPage()
    {
        InitializeComponent();
        _onInstallerChanged = () => Dispatcher.BeginInvoke(ShowInstallState);
        _onInstallerLog = line => Dispatcher.BeginInvoke(() => AppendInstallLog(line));
        Action onFlow = () => Dispatcher.BeginInvoke(async () => await RefreshAsync());
        Loaded += async (_, _) =>
        {
            SshInstaller.Changed += _onInstallerChanged;
            SshInstaller.LogLine += _onInstallerLog;
            InstallFlowChanged += onFlow;
            InstallLog.Text = string.Join(Environment.NewLine, SshInstaller.Log);
            ShowInstallState();
            await RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            SshInstaller.Changed -= _onInstallerChanged;
            SshInstaller.LogLine -= _onInstallerLog;
            InstallFlowChanged -= onFlow;
        };
    }

    private static bool InstallBusyNow => _installFlow is { IsCompleted: false } || SshInstaller.IsRunning;

    private void ShowInstallState()
    {
        bool busy = InstallBusyNow;
        bool hasLog = SshInstaller.Log.Count > 0;
        bool installed = SshService.IsInstalled;
        InstallCard.Visibility = installed && !busy ? Visibility.Collapsed : Visibility.Visible;
        SshToggle.IsEnabled = installed && !busy;
        InstallBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = !busy;
        InstallProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        InstallProgress.IsIndeterminate = SshInstaller.Percent < 0;
        if (SshInstaller.Percent >= 0) InstallProgress.Value = SshInstaller.Percent;
        InstallLogExpander.Visibility = hasLog ? Visibility.Visible : Visibility.Collapsed;
        if (busy || SshInstaller.LastError != null)
            InstallDesc.Text = SshInstaller.Stage;
    }

    private void AppendInstallLog(string line)
    {
        InstallLog.AppendText((InstallLog.Text.Length > 0 ? Environment.NewLine : "") + line);
        InstallLog.ScrollToEnd();
        InstallLogExpander.Visibility = Visibility.Visible;
    }

    /// <summary>安装并启动 sshd。在 UI 线程启动，后续回到 UI 线程弹提示；与具体页面实例无关。</summary>
    private static async Task InstallFlowAsync()
    {
        try
        {
            await SshInstaller.RunAsync();
            var warn = await SshService.EnableAsync(true);
            RemotePage.Done(L.T("OpenSSH 服务器已安装并启动"), warn);
        }
        catch (Exception ex) { Notify.Error(L.T("OpenSSH 安装失败"), ex.Message); }
        finally { InstallFlowChanged?.Invoke(); }
    }

    private async Task RefreshAsync()
    {
        bool installed = SshService.IsInstalled;
        var (status, startType) = await SshService.GetStatusAsync();
        bool running = status == "Running";
        int port = SshService.GetPort();

        ShowInstallState();
        SshToggle.IsChecked = running;
        PortText.Text = port.ToString();
        if (string.IsNullOrEmpty(PortBox.Text)) PortBox.Text = port.ToString();
        ConfigPathText.Text = L.F("配置文件：{0}", SshService.ConfigPath);

        bool listening = running && await Task.Run(() => RdpService.IsListening(port));
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

        await RemotePage.ShowPasswordWarningAsync(PasswordBar, PasswordFixButton);
        var lan = NetworkService.Primary(await Task.Run(NetworkService.GetLanAddresses));
        CommandText.Text = SshService.ConnectCommand(lan?.Ip ?? L.T("本机IP"), port);
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (!InstallBusyNow)
        {
            InstallLog.Text = "";
            _installFlow = InstallFlowAsync();
        }
        ShowInstallState();
    }

    private void GoAccounts_Click(object sender, RoutedEventArgs e) => RemotePage.GoAccounts();

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

    private async void CopyCmd_Click(object sender, RoutedEventArgs e)
    {
        if (await RemoteInfo.CopyToClipboardAsync(CommandText.Text)) Notify.Success(L.T("已复制"), CommandText.Text);
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

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RdpHelper.Services;
using Wpf.Ui.Controls;

namespace RdpHelper.Views;

public partial class RemotePage : Page
{
    public RemotePage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var ed = RdpService.GetEdition();
        MachineText.Text = Environment.MachineName;
        OsText.Text = L.F("{0} {1}（内部版本 {2}）· 用户 {3}", ed.ProductName, ed.DisplayVersion, ed.Build, RemoteInfo.UserName());
        if (ed.IsHome)
        {
            EditionBar.Message = L.F("{0} 家庭版没有远程桌面服务端，只能连接别人，不能被别人连接。可升级到专业版，或使用 SSH 功能。", ed.ProductName);
            EditionBar.IsOpen = true;
        }
        RemoteSessionBar.IsOpen = RdpService.IsInRemoteSession;

        bool on = RdpService.IsEnabled();
        int port = RdpService.GetPort();
        RdpToggle.IsChecked = on;
        RdpToggle.IsEnabled = !ed.IsHome;
        NlaToggle.IsChecked = RdpService.IsNlaEnabled();
        PortText.Text = port.ToString();
        if (string.IsNullOrEmpty(PortBox.Text)) PortBox.Text = port.ToString();

        bool listening = await Task.Run(() => RdpService.IsListening(port));
        SetStatus(StatusDot, StatusText, on, listening,
            L.F("远程桌面已开启 · 正在监听端口 {0}", port),
            L.F("远程桌面已开启 · 端口 {0} 暂未监听（服务启动中或被占用）", port),
            L.T("远程桌面已关闭"));

        var lans = await Task.Run(NetworkService.GetLanAddresses);
        LanList.ItemsSource = lans;
        NoLanText.Visibility = lans.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        bool ssh = await SshService.IsRunningAsync();
        PreviewBox.Text = RemoteInfo.Build(ssh);
    }

    private async void RdpToggle_Click(object sender, RoutedEventArgs e)
    {
        bool want = RdpToggle.IsChecked == true;
        if (!want && RdpService.IsInRemoteSession &&
            !Confirm(L.T("你正在远程使用这台电脑，关闭后将无法再远程连回来。确定关闭吗？")))
        {
            RdpToggle.IsChecked = true;
            return;
        }
        await Busy(RdpBusy, RdpToggle, async () =>
        {
            var warn = await RdpService.EnableAsync(want);
            if (want) await RdpService.WaitListeningAsync(RdpService.GetPort(), true, 10_000);
            Done(want ? L.T("远程桌面已开启") : L.T("远程桌面已关闭"), warn);
        });
    }

    private void NlaToggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RdpService.SetNla(NlaToggle.IsChecked == true);
            Notify.Success(NlaToggle.IsChecked == true ? L.T("已开启网络级别身份验证") : L.T("已关闭网络级别身份验证"));
        }
        catch (Exception ex) { Notify.Error(L.T("设置失败"), ex.Message); }
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
        PortBox.Text = RdpService.DefaultPort.ToString();
        await ChangePort(RdpService.DefaultPort);
    }

    private async Task ChangePort(int port)
    {
        if (port == RdpService.GetPort())
        {
            Notify.Info(L.T("端口没有变化"), L.F("当前已经是 {0}", port));
            return;
        }
        if (RdpService.IsInRemoteSession && !Confirm(L.F("修改端口会重启远程桌面服务，你当前的远程连接会断开。\n之后请用  IP:{0}  重新连接。继续吗？", port)))
            return;
        await Busy(PortBusy, null, async () =>
        {
            var warn = await RdpService.SetPortAsync(port);
            Done(L.F("远程桌面端口已改为 {0}", port), warn);
        });
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteInfo.CopyToClipboard(PreviewBox.Text))
            Notify.Success(L.T("已复制远程信息"), L.T("可以直接粘贴发给对方（不含密码）"));
        else
            Notify.Error(L.T("复制失败"), L.T("剪贴板被其他程序占用，请重试"));
    }

    private void CopyIp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string ip })
        {
            int port = RdpService.GetPort();
            var text = port == RdpService.DefaultPort ? ip : $"{ip}:{port}";
            if (RemoteInfo.CopyToClipboard(text)) Notify.Success(L.T("已复制"), text);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OpenSysSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:remotedesktop") { UseShellExecute = true }); }
        catch { Process.Start(new ProcessStartInfo("SystemPropertiesRemote.exe") { UseShellExecute = true }); }
    }

    // ---------- 公共小工具 ----------

    private async Task Busy(ProgressRing ring, UIElement? control, Func<Task> action)
    {
        ring.Visibility = Visibility.Visible;
        if (control != null) control.IsEnabled = false;
        IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { Notify.Error(L.T("操作失败"), ex.Message); }
        finally
        {
            ring.Visibility = Visibility.Collapsed;
            if (control != null) control.IsEnabled = true;
            IsEnabled = true;
            await RefreshAsync();
        }
    }

    internal static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71)));
    internal static readonly Brush Orange = Freeze(new SolidColorBrush(Color.FromRgb(0xF3, 0x9C, 0x12)));
    internal static readonly Brush Gray = Freeze(new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)));

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    internal static void SetStatus(System.Windows.Shapes.Ellipse dot, System.Windows.Controls.TextBlock text,
        bool on, bool ok, string onOk, string onBad, string off)
    {
        dot.Fill = !on ? Gray : ok ? Green : Orange;
        text.Text = !on ? off : ok ? onOk : onBad;
    }

    internal static void Done(string title, string warn)
    {
        if (string.IsNullOrWhiteSpace(warn)) Notify.Success(title);
        else Notify.Warn(L.F("{0}（有警告）", title), warn);
    }

    internal static bool Confirm(string message) =>
        System.Windows.MessageBox.Show(message, "RdpHelper", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;
}

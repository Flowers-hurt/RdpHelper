using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RdpHelper.Services;
using RdpHelper.Services.Tunnels;
using Wpf.Ui.Controls;

namespace RdpHelper.Views;

public partial class TunnelCard : UserControl
{
    private readonly TunnelProvider _p;
    private readonly Action<string> _onLog;
    private readonly Action _onState;

    public TunnelCard(TunnelProvider provider, SymbolRegular icon)
    {
        InitializeComponent();
        _p = provider;
        HeaderIcon.Symbol = icon;
        NameText.Text = provider.Name;
        SummaryText.Text = provider.Summary;
        NotesText.Text = provider.Notes;
        TokenPanel.Visibility = provider.NeedsToken ? Visibility.Visible : Visibility.Collapsed;
        TokenHint.Text = provider.TokenHint;

        _onLog = line => Dispatcher.BeginInvoke(() => AppendLog(line));
        _onState = () => Dispatcher.BeginInvoke(UpdateState);
        Loaded += (_, _) =>
        {
            _p.Log += _onLog;
            _p.StateChanged += _onState;
            UpdateState();
        };
        Unloaded += (_, _) =>
        {
            _p.Log -= _onLog;
            _p.StateChanged -= _onState;
        };
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        if (LogBox.LineCount > 400) LogBox.Text = string.Join(Environment.NewLine, LogBox.Text.Split('\n').TakeLast(300));
        LogBox.ScrollToEnd();
    }

    private void UpdateState()
    {
        bool installed = _p.IsInstalled;
        bool running = _p.IsRunning;
        PathText.Text = installed ? "已下载：" + _p.ExePath : "未下载 · 将下载到 " + _p.ToolDir + "（不随安装包分发，按需下载）";
        DownloadButton.Content = installed ? "重新下载 / 更新" : "下载组件";
        UninstallButton.IsEnabled = installed && !running;
        StartButton.IsEnabled = installed && !running;
        StopButton.IsEnabled = running;
        TargetBox.IsEnabled = !running;

        if (running && _p.PublicAddress != null)
        {
            StateDot.Fill = RemotePage.Green;
            StateText.Text = "运行中";
            AddressPanel.Visibility = Visibility.Visible;
            AddressText.Text = _p.PublicAddress;
            Expander.IsExpanded = true;
        }
        else
        {
            StateDot.Fill = running ? RemotePage.Orange : installed ? RemotePage.Gray : RemotePage.Gray;
            StateText.Text = running ? "连接中…" : installed ? "已下载" : "未下载";
            AddressPanel.Visibility = Visibility.Collapsed;
        }

        if (_p is NgrokProvider ng)
            TokenState.Text = ng.HasToken ? "✓ 已配置" : "未配置";
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        DownloadButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        Expander.IsExpanded = true;
        try
        {
            var progress = new Progress<double>(v =>
            {
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = v;
            });
            await _p.InstallAsync(progress);
            var ver = await _p.GetVersionAsync();
            Notify.Success(_p.Name + " 下载完成", ver);
        }
        catch (Exception ex)
        {
            Notify.Error("下载失败", ex.Message + "\n网络不好时可以手动下载后点「导入本地 exe」。");
            AppendLog("下载失败：" + ex.Message);
        }
        finally
        {
            DownloadButton.IsEnabled = true;
            DownloadProgress.Visibility = Visibility.Collapsed;
            UpdateState();
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 " + _p.ExeName,
            Filter = $"{_p.ExeName}|{_p.ExeName}|可执行文件 (*.exe)|*.exe",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _p.ImportExe(dlg.FileName);
            Notify.Success("已导入 " + _p.Name);
        }
        catch (Exception ex) { Notify.Error("导入失败", ex.Message); }
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        _p.Uninstall();
        Notify.Info("已删除 " + _p.Name + " 组件");
    }

    private void Homepage_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_p.Homepage) { UseShellExecute = true });

    private async void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        if (_p is not NgrokProvider ng) return;
        if (!ng.IsInstalled) { Notify.Warn("请先下载 ngrok"); return; }
        try
        {
            await ng.SaveTokenAsync(TokenBox.Password);
            TokenBox.Password = "";
            Notify.Success("Authtoken 已保存");
        }
        catch (Exception ex) { Notify.Error("保存失败", ex.Message); }
        UpdateState();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var target = TargetBox.SelectedIndex == 1 ? RemoteKind.Ssh : RemoteKind.Rdp;
        int port;
        if (target == RemoteKind.Rdp)
        {
            port = RdpService.GetPort();
            if (!RdpService.IsEnabled()) Notify.Warn("远程桌面还没开启", "穿透已启动，但需要先在「远程桌面」页开启才能连接");
        }
        else
        {
            port = SshService.GetPort();
            if (!await SshService.IsRunningAsync()) Notify.Warn("SSH 服务还没开启", "穿透已启动，但需要先在「SSH」页开启才能连接");
        }
        if (_p is NgrokProvider { HasToken: false })
        {
            Notify.Warn("请先填写 ngrok Authtoken");
            return;
        }

        StartBusy.Visibility = Visibility.Visible;
        StartButton.IsEnabled = false;
        Expander.IsExpanded = true;
        try
        {
            var addr = await _p.StartAsync(port, target);
            Notify.Success(_p.Name + " 已连通", addr);
        }
        catch (Exception ex) { Notify.Error(_p.Name + " 启动失败", ex.Message); }
        finally
        {
            StartBusy.Visibility = Visibility.Collapsed;
            UpdateState();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _p.Stop();
        Notify.Info(_p.Name + " 已停止");
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (_p.PublicAddress != null && RemoteInfo.CopyToClipboard(_p.PublicAddress))
            Notify.Success("已复制公网地址", _p.PublicAddress);
    }

    private async void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var text = RemoteInfo.Build(await SshService.IsRunningAsync());
        if (RemoteInfo.CopyToClipboard(text)) Notify.Success("已复制完整连接信息", "包含局域网和公网地址，可直接发给对方");
    }
}

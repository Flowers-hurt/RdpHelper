using System.Windows;
using RdpHelper.Services;
using RdpHelper.Views;
using Wpf.Ui.Controls;

namespace RdpHelper;

public partial class MainWindow : FluentWindow
{
    public MainWindow(Type? startPage = null)
    {
        InitializeComponent();
        App.ApplyAppearance(this);
        Notify.Init(SnackbarPresenter);
        // 打开时适配所在屏幕的工作区并居中（还没显示，不会闪）；显示后再按真实像素复核一次
        SourceInitialized += (_, _) => { if (AutoPlace) WindowFit.FitAndCenter(this); };
        ContentRendered += (_, _) => WindowFit.EnsureInside(this);
        Loaded += (_, _) => RootNavigation.Navigate(startPage ?? typeof(RemotePage));
    }

    /// <summary>为 false 时保留调用方设置的位置（切换语言重建窗口时沿用旧窗口位置）。</summary>
    public bool AutoPlace { get; set; } = true;

    public bool NavigateTo(Type pageType) => RootNavigation.Navigate(pageType);

    /// <summary>为 true 时直接关闭（切换语言替换窗口时用），不收进托盘。</summary>
    public bool AllowClose { get; set; }

    /// <summary>有内网穿透 / 访问通道在运行：关掉程序它们就断了，所以关闭窗口时收进托盘。</summary>
    public static bool TunnelsActive =>
        Environment.GetEnvironmentVariable("RDPHELPER_TEST_FAKE_TUNNEL") == "1"
        || Services.Tunnels.TunnelManager.All.Any(p => p.IsRunning)
        || Services.Tunnels.TunnelManager.Cloudflare.IsAccessRunning;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose && !App.IsExiting && TunnelsActive && App.HideToTray(this))
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}

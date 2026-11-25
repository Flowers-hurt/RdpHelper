using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using RdpHelper.Services;
using RdpHelper.Services.Tunnels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RdpHelper.Views;

/// <summary>
/// 托盘右键菜单：不用 WinForms 老式菜单，而是一个 Win11 风格的弹出面板——
/// 系统亚克力（和开始菜单 / 右键菜单同一种 DWM 材质）、圆角、跟随主题，顶部显示运行状态。
/// </summary>
public partial class TrayMenu : Window
{
    private static TrayMenu? _current;
    private readonly POINT _anchor;
    private bool _closing;

    private TrayMenu(POINT anchor)
    {
        _anchor = anchor;
        InitializeComponent();
        SubtitleText.Text = L.F("版本 {0}", AppPaths.Version);
        // 显示前（SourceInitialized：窗口句柄已建、还没画）就量好尺寸并贴到鼠标旁边，第一次绘制就在最终位置。
        // 之前先放屏幕外再移过来，偶尔移动后只重绘了一部分，菜单大片空白。
        SourceInitialized += (_, _) => { ApplyChrome(); PlaceNearAnchor(); };
        Loaded += (_, _) => { SetForegroundWindow(new WindowInteropHelper(this).Handle); Activate(); OpenRow.Focus(); };
        // DWM 亚克力 + 透明合成时，WPF 偶尔只呈现了局部脏区（CI 上截到过只画出一行的空白菜单）。
        // 首帧后和状态异步刷新后各请求一次整窗重绘，保证内容完整。
        ContentRendered += (_, _) => { ForceRedraw(); Dispatcher.InvokeAsync(ForceRedraw, System.Windows.Threading.DispatcherPriority.Background); };
        Deactivated += (_, _) => SafeClose();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) SafeClose(); };
        FillStatus();
    }

    /// <summary>在鼠标位置（或指定像素坐标）弹出；已打开则关掉（再点一次托盘图标就收起）。</summary>
    public static void Toggle(System.Drawing.Point? anchorPx = null)
    {
        if (_current != null) { _current.SafeClose(); return; }
        POINT p;
        if (anchorPx is { } a) p = new POINT { X = a.X, Y = a.Y };
        else GetCursorPos(out p);
        _current = new TrayMenu(p);
        _current.Closed += (_, _) => _current = null;
        _current.Show();
    }

    public static bool IsOpen => _current != null;

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private async void FillStatus()
    {
        var on = TryFindResource("SystemFillColorSuccessBrush") as Brush ?? Brushes.LimeGreen;
        var off = TryFindResource("TextFillColorDisabledBrush") as Brush ?? Brushes.Gray;

        bool rdp = false;
        try { rdp = RdpService.IsEnabled(); } catch { }
        RdpDot.Fill = rdp ? on : off;
        RdpText.Text = rdp ? L.F("已开启 · 端口 {0}", SafePort(RdpService.GetPort)) : L.T("未开启");

        var tunnels = TunnelManager.All.Where(t => t.IsRunning).ToList();
        TunnelDot.Fill = tunnels.Count > 0 ? on : off;
        TunnelText.Text = tunnels.Count > 0
            ? string.Join(" · ", tunnels.Select(t => t.PublicAddress ?? t.Name))
            : L.T("未运行");
        TunnelText.ToolTip = TunnelText.Text;

        SshDot.Fill = off;
        SshText.Text = "…";
        bool ssh = false;
        try { ssh = SshService.IsInstalled && await SshService.IsRunningAsync(); } catch { }
        SshDot.Fill = ssh ? on : off;
        SshText.Text = ssh ? L.F("运行中 · 端口 {0}", SafePort(SshService.GetPort))
            : SshService.IsInstalled ? L.T("未开启") : L.T("未安装");
        ForceRedraw();
    }

    private void ForceRedraw()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Root.InvalidateVisual();
        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
    }

    private static string SafePort(Func<int> get)
    {
        try { return get().ToString(); } catch { return "?"; }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        SafeClose();
        App.ShowFromTray();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SafeClose();
        App.ShowFromTray();
        if (Application.Current.MainWindow is MainWindow w) w.NavigateTo(typeof(SettingsPage));
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        bool ssh = false;
        try { ssh = SshService.IsInstalled && await SshService.IsRunningAsync(); } catch { }
        bool ok = await RemoteInfo.CopyToClipboardAsync(RemoteInfo.Build(ssh));
        CopyIcon.Symbol = ok ? SymbolRegular.Checkmark24 : SymbolRegular.ErrorCircle24;
        CopyText.Text = ok ? L.T("已复制到剪贴板") : L.T("复制失败");
        await Task.Delay(900);
        SafeClose();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        SafeClose();
        App.RequestExit();
    }

    // ---------- 外观：系统亚克力 + 圆角 + 深浅色 ----------

    private void ApplyChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // 不出现在 Alt+Tab 里
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

        bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
        int corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        // Win11 22H2+：DWM「瞬态窗口」亚克力，正是系统右键菜单 / 弹出面板用的材质
        bool acrylic = false;
        if (Environment.OSVersion.Version.Build >= 22621)
        {
            int type = DWMSBT_TRANSIENTWINDOW;
            acrylic = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) == 0;
        }
        if (acrylic && HwndSource.FromHwnd(hwnd) is { } src)
        {
            src.CompositionTarget.BackgroundColor = Colors.Transparent;
            var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
            Background = Brushes.Transparent;
        }
        else
        {
            // 旧系统：用主题的纯色背景
            Background = TryFindResource("ApplicationBackgroundBrush") as Brush
                ?? new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF9, 0xF9, 0xF9));
            Root.BorderBrush = TryFindResource("SurfaceStrokeColorFlyoutBrush") as Brush;
            Root.BorderThickness = new Thickness(1);
        }
    }

    /// <summary>贴着鼠标弹出，并整体收在该显示器工作区内（任务栏在哪边都不会被挡）。</summary>
    private void PlaceNearAnchor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var mon = MonitorFromPoint(_anchor, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref mi);
        var wa = mi.rcWork;
        GetDpiForMonitor(mon, 0, out uint dpiX, out uint dpiY);
        double sx = dpiX / 96.0, sy = dpiY / 96.0;
        Measure(new Size(Width, double.PositiveInfinity));
        int w = (int)Math.Ceiling(Width * sx), h = (int)Math.Ceiling(DesiredSize.Height * sy);
        const int gap = 8;

        // 默认在鼠标左上方（任务栏在底部时最自然）；放不下就翻到另一侧
        int x = _anchor.X - w;
        int y = _anchor.Y - h - gap;
        if (x < wa.Left + gap) x = _anchor.X;
        if (y < wa.Top + gap) y = _anchor.Y + gap;
        x = Math.Clamp(x, wa.Left + gap, Math.Max(wa.Left + gap, wa.Right - w - gap));
        y = Math.Clamp(y, wa.Top + gap, Math.Max(wa.Top + gap, wa.Bottom - h - gap));
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // ---------- Win32 ----------
    private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2, DWMSBT_TRANSIENTWINDOW = 3;
    private const uint MONITOR_DEFAULTTONEAREST = 2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MARGINS { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    private const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100, RDW_FRAME = 0x400;
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr rgn, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);
}

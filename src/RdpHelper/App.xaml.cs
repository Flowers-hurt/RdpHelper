using System.Windows;
using System.Windows.Threading;
using RdpHelper.Services;
using RdpHelper.Services.Tunnels;
using RdpHelper.Testing;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RdpHelper;

public partial class App : Application
{
    private MainWindow? _main;
    private TrayIcon? _tray;
    private bool _exiting;

    /// <summary>正在退出（托盘菜单「退出」或关闭窗口）时为 true，窗口不再拦截关闭。</summary>
    public static bool IsExiting => Current is App { _exiting: true };

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        // 语言：--lang zh|en 优先（自检 / 截图用），否则按设置（默认跟随系统）
        int li = Array.IndexOf(args, "--lang");
        string? langArg = li >= 0 && li + 1 < args.Length ? args[li + 1] : null;
        if (li >= 0) args = args.Where((_, i) => i != li && i != li + 1).ToArray();
        L.Apply(langArg ?? AppSettings.Current.Language);

        // 命令行自检（CI 在真实 Windows 上跑）：RdpHelper.exe --selftest report.json
        if (args.Length >= 1 && args[0] == "--selftest")
        {
            if (langArg == null) L.Apply(L.Zh);
            int code = await SelfTest.RunAsync(args.Length > 1 ? args[1] : "selftest.json");
            Shutdown(code);
            return;
        }

        bool guiTest = args.Length >= 1 && args[0] == "--gui-selftest";
        if (!guiTest)
        {
            bool first;
            try { first = SingleInstance.Acquire(StartupLog); }
            catch (Exception ex) { StartupLog("single-instance check failed: " + ex.Message); first = true; }
            if (!first)
            {
                Shutdown();
                return;
            }
            // CI 用：模拟「窗口没了但进程还在」的残留实例（占着锁、不响应唤醒），验证再次启动能自动恢复
            if (Environment.GetEnvironmentVariable("RDPHELPER_TEST_ZOMBIE") == "1") return;
            SingleInstance.Listen(ShowFromTray);
        }

        DispatcherUnhandledException += OnUnhandled;

        var window = new MainWindow();
        ShowMain(window);
        if (!guiTest)
        {
            try { _tray = new TrayIcon(ShowFromTray, RequestExit); }
            catch (Exception ex) { StartupLog("tray icon failed: " + ex.Message); }
        }

        if (guiTest)
        {
            int code = await GuiSelfTest.RunAsync(window, args.Length > 1 ? args[1] : "screenshots");
            Shutdown(code);
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Notify.Error(L.T("出错了"), e.Exception.Message);
    }

    private void ShowMain(MainWindow window)
    {
        MainWindow = window;
        _main = window;
        // 切换语言时会换一个新窗口，旧窗口关闭不应退出程序。
        // 用自己的字段判断：WPF 在触发 Closed 前可能已清空 Application.MainWindow，
        // 之前据此判断导致点关闭后进程残留（再打开提示「已经在运行」却看不到界面）。
        window.Closed += (_, _) => { if (ReferenceEquals(_main, window)) RequestExit(); };
        window.Show();
    }

    /// <summary>收进托盘（有穿透在运行时关闭窗口走这里，关掉程序穿透会断）。</summary>
    public static bool HideToTray(MainWindow window)
    {
        var app = (App)Current;
        if (app._tray == null) return false;
        window.Hide();
        app._tray.NotifyHidden();
        return true;
    }

    /// <summary>托盘点击 / 再次双击程序图标：把主界面显示到前台。</summary>
    public static void ShowFromTray()
    {
        var app = (App)Current;
        if (app._exiting || app._main is not { } w) return;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        // 从别的进程唤醒时 Windows 可能不让抢前台，短暂置顶确保看得见
        w.Topmost = true;
        w.Topmost = false;
        w.Focus();
        WindowFit.EnsureInside(w);
    }

    /// <summary>真正退出：停穿透、去托盘图标、释放单实例锁，并保证进程结束。</summary>
    public static void RequestExit()
    {
        var app = (App)Current;
        if (app._exiting) return;
        app._exiting = true;
        app._tray?.Dispose();
        app._tray = null;
        // 万一有后台线程拖住进程，5 秒后强制结束，避免再出现「看不到界面却提示已在运行」
        new Thread(() => { Thread.Sleep(5000); Environment.Exit(0); }) { IsBackground = true }.Start();
        app.Shutdown();
    }

    private static void StartupLog(string line)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var f = Path.Combine(AppPaths.Root, "startup.log");
            if (File.Exists(f) && new FileInfo(f).Length > 256 * 1024) File.Delete(f);
            File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {line}{Environment.NewLine}");
        }
        catch { /* 日志失败不影响启动 */ }
    }

    /// <summary>切换界面语言：按新语言重建主窗口，停在设置页；穿透进程等状态是静态的，不受影响。</summary>
    public static void ReloadUi()
    {
        var app = (App)Current;
        if (app.MainWindow is not MainWindow old) return;
        var w = new MainWindow(typeof(Views.SettingsPage))
        {
            AutoPlace = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = old.Left,
            Top = old.Top,
            Width = old.Width,
            Height = old.Height,
        };
        var state = old.WindowState;
        app.ShowMain(w);
        w.WindowState = state;
        old.AllowClose = true;
        old.Close();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TunnelManager.StopAll();
        _tray?.Dispose();
        SingleInstance.Release();
        base.OnExit(e);
    }

    public static WindowBackdropType BackdropFromSetting(string s) => s switch
    {
        "mica" => WindowBackdropType.Mica,
        "tabbed" => WindowBackdropType.Tabbed,
        "none" => WindowBackdropType.None,
        _ => WindowBackdropType.Acrylic,
    };

    /// <summary>按设置应用主题与背景材质（默认：跟随系统 + 亚克力毛玻璃）。</summary>
    public static void ApplyAppearance(FluentWindow window)
    {
        var s = AppSettings.Current;
        var backdrop = BackdropFromSetting(s.Backdrop);
        try { SystemThemeWatcher.UnWatch(window); } catch { /* 未监听 */ }

        if (s.Theme == "light" || s.Theme == "dark")
        {
            ApplicationThemeManager.Apply(s.Theme == "light" ? ApplicationTheme.Light : ApplicationTheme.Dark, backdrop, true);
        }
        else
        {
            ApplicationThemeManager.ApplySystemTheme(true);
            SystemThemeWatcher.Watch(window, backdrop, true);
        }
        window.WindowBackdropType = backdrop;
    }
}

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
    private Mutex? _mutex;

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
            _mutex = new Mutex(true, "Global\\RdpHelper.SingleInstance", out bool created);
            if (!created)
            {
                System.Windows.MessageBox.Show(L.T("RdpHelper 已经在运行了。"), "RdpHelper", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }

        DispatcherUnhandledException += OnUnhandled;

        var window = new MainWindow();
        ShowMain(window);

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
        // 切换语言时会换一个新窗口，旧窗口关闭不应退出程序
        window.Closed += (_, _) => { if (ReferenceEquals(MainWindow, window)) Shutdown(); };
        window.Show();
    }

    /// <summary>切换界面语言：按新语言重建主窗口，停在设置页；穿透进程等状态是静态的，不受影响。</summary>
    public static void ReloadUi()
    {
        var app = (App)Current;
        if (app.MainWindow is not MainWindow old) return;
        var w = new MainWindow(typeof(Views.SettingsPage))
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = old.Left,
            Top = old.Top,
            Width = old.Width,
            Height = old.Height,
        };
        var state = old.WindowState;
        app.ShowMain(w);
        w.WindowState = state;
        old.Close();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TunnelManager.StopAll();
        _mutex?.Dispose();
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

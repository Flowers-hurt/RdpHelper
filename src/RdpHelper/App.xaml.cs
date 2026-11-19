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

        // 命令行自检（CI 在真实 Windows 上跑）：RdpHelper.exe --selftest report.json
        if (args.Length >= 1 && args[0] == "--selftest")
        {
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
                System.Windows.MessageBox.Show("RdpHelper 已经在运行了。", "RdpHelper", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }

        DispatcherUnhandledException += OnUnhandled;

        var window = new MainWindow();
        MainWindow = window;
        window.Closed += (_, _) => Shutdown();
        window.Show();

        if (guiTest)
        {
            int code = await GuiSelfTest.RunAsync(window, args.Length > 1 ? args[1] : "screenshots");
            Shutdown(code);
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Notify.Error("出错了", e.Exception.Message);
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

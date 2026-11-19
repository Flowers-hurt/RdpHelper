using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RdpHelper.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RdpHelper.Testing;

/// <summary>
/// 界面自检 + 高清截图：依次打开每个页面（浅色 / 深色），以 2 倍分辨率渲染成 PNG。
/// 用法：RdpHelper.exe --gui-selftest 输出目录
/// CI 上的 Windows Server 没有 Win11 亚克力，这里用「彩色壁纸 + 半透明主题色」合成，
/// 模拟毛玻璃窗口在桌面上的观感。
/// </summary>
public static class GuiSelfTest
{
    private static readonly (Type page, string name)[] Pages =
    {
        (typeof(RemotePage), "remote"),
        (typeof(SshPage), "ssh"),
        (typeof(AccessPage), "access"),
        (typeof(TunnelPage), "tunnel"),
        (typeof(SettingsPage), "settings"),
    };

    public static async Task<int> RunAsync(MainWindow window, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var log = new StringBuilder();
        int failed = 0;
        window.Width = 1180;
        window.Height = 820;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 0;
        window.Top = 0;
        await Settle(2500);

        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
        {
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Acrylic, true);
            await Settle(800);
            foreach (var (page, name) in Pages)
            {
                try
                {
                    window.NavigateTo(page); // 已在该页时返回 false，不算失败
                    bool ok = true;
                    await Settle(name is "remote" or "ssh" ? 6000 : 2500);
                    var file = Path.Combine(outDir, $"{name}-{theme.ToString().ToLowerInvariant()}.png");
                    Render(window, file, theme == ApplicationTheme.Dark);
                    log.AppendLine($"{(ok ? "OK  " : "FAIL")} {theme} {name} -> {file}");
                    if (!ok) failed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    log.AppendLine($"FAIL {theme} {name}: {ex}");
                }
            }
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, "gui-selftest.txt"), log.ToString());
        return failed == 0 ? 0 : 1;
    }

    private static async Task Settle(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(50);
        }
    }

    private static void Render(Window window, string file, bool dark)
    {
        const double scale = 2.0;
        var root = (FrameworkElement)window.Content;
        double w = root.ActualWidth, h = root.ActualHeight;
        const double pad = 56;
        double W = w + pad * 2, H = h + pad * 2;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // 「桌面壁纸」
            var wall = new Rect(0, 0, W, H);
            dc.DrawRectangle(new LinearGradientBrush(
                dark ? Color.FromRgb(0x0B, 0x1E, 0x4A) : Color.FromRgb(0x9C, 0xC8, 0xFF),
                dark ? Color.FromRgb(0x3A, 0x10, 0x4F) : Color.FromRgb(0xF6, 0xC6, 0xE8), 35), null, wall);
            dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(0xC0, 0x00, 0x9E, 0xFF), Colors.Transparent), null, new Point(W * 0.18, H * 0.25), W * 0.35, H * 0.45);
            dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(0xA0, 0xFF, 0x6A, 0x3D), Colors.Transparent), null, new Point(W * 0.85, H * 0.8), W * 0.35, H * 0.5);

            // 窗口：半透明亚克力底 + 阴影边框 + 内容
            var win = new Rect(pad, pad, w, h);
            var tint = dark ? Color.FromArgb(0xD0, 0x20, 0x20, 0x20) : Color.FromArgb(0xC8, 0xF3, 0xF3, 0xF3);
            var shadow = new Rect(pad - 1, pad + 4, w + 2, h + 2);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), null, shadow, 10, 10);
            dc.PushClip(new RectangleGeometry(win, 8, 8));
            dc.DrawRectangle(new SolidColorBrush(tint), null, win);
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, win);
            dc.Pop();
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)), 1), win, 8, 8);
        }

        var bmp = new RenderTargetBitmap((int)(W * scale), (int)(H * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}

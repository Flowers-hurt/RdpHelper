using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RdpHelper.Services;
using RdpHelper.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RdpHelper.Testing;

/// <summary>
/// 界面自检 + 高清截图：依次打开每个页面（浅色 / 深色），以 2 倍分辨率渲染成 PNG。
/// 用法：RdpHelper.exe --gui-selftest 输出目录 [--lang zh|en]
/// 英文模式下还会检查：界面上不能残留中文、不能有缺失译文；中文模式最后会实测一次「切换到英文」。
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

        // RDPHELPER_GUI_PAGES=remote,ssh 只截指定页面（家庭版模拟等补充截图用）
        var only = (Environment.GetEnvironmentVariable("RDPHELPER_GUI_PAGES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var pages = only.Length == 0 ? Pages : Pages.Where(p => only.Contains(p.name)).ToArray();

        if (Environment.GetEnvironmentVariable("RDPHELPER_GUI_INSTALLTEST") == "1")
            failed += await InstallProgressTestAsync(window, outDir, log);

        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
        {
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Acrylic, true);
            await Settle(800);
            foreach (var (page, name) in pages)
            {
                try
                {
                    window.NavigateTo(page); // 已在该页时返回 false，不算失败
                    await Settle(name is "remote" or "ssh" ? 6000 : 2500);
                    var file = Path.Combine(outDir, $"{name}-{theme.ToString().ToLowerInvariant()}.png");
                    Render(window, file, theme == ApplicationTheme.Dark);
                    var leftovers = L.IsEnglish ? ChineseTexts(window) : new List<string>();
                    bool ok = leftovers.Count == 0;
                    log.AppendLine($"{(ok ? "OK  " : "FAIL")} {L.Lang} {theme} {name} -> {file}");
                    foreach (var t in leftovers) log.AppendLine("     未翻译：" + t);
                    if (!ok) failed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    log.AppendLine($"FAIL {theme} {name}: {ex}");
                }
            }
        }
        if (L.IsEnglish)
        {
            foreach (var k in L.Missing.Keys) { failed++; log.AppendLine("FAIL 缺少英文译文：" + k); }
        }
        else if (only.Length == 0)
        {
            // 实测设置页的语言切换：重建窗口后应停在设置页、全部是英文
            try
            {
                L.Apply(L.En);
                App.ReloadUi();
                await Settle(3000);
                var w = (MainWindow)Application.Current.MainWindow;
                var texts = AllTexts(w);
                var cn = ChineseTexts(w);
                bool ok = !ReferenceEquals(w, window) && w.Title == "RdpHelper" && texts.Contains("Language") && cn.Count == 0;
                Render(w, Path.Combine(outDir, "switch-to-english.png"), false);
                log.AppendLine($"{(ok ? "OK  " : "FAIL")} 切换语言 zh→en：新窗口={!ReferenceEquals(w, window)} 标题={w.Title} 旧窗口已关={!window.IsLoaded} 残留中文={cn.Count}");
                foreach (var t in cn) log.AppendLine("     未翻译：" + t);
                if (!ok) failed++;
            }
            catch (Exception ex) { failed++; log.AppendLine("FAIL 切换语言：" + ex); }
            finally { L.Apply(L.Zh); }
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, "gui-selftest.txt"), log.ToString());
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 复现「安装 OpenSSH 时切到别的页面再回来，转圈停了、看不到进度」：
    /// 先卸载自带 MSI 装的 OpenSSH（引擎自检已装过）→ 打开 SSH 页点「一键安装」→ 切到远程桌面页 → 再切回 SSH 页，
    /// 新页面实例上进度条和阶段文字必须仍在显示；最后等安装 + 启动完成、安装卡片自动收起。
    /// </summary>
    private static async Task<int> InstallProgressTestAsync(MainWindow window, string outDir, StringBuilder log)
    {
        try
        {
            ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Acrylic, true);
            var msi = SshInstaller.BundledMsi ?? throw new InvalidOperationException("redist 里没有自带的 OpenSSH MSI");
            var un = await Shell.RunAsync("msiexec.exe", $"/x \"{msi}\" /qn /norestart", 10 * 60_000);
            bool removed = !SshService.IsInstalled;
            log.AppendLine($"     卸载 OpenSSH（msiexec /x exit {un.ExitCode}）后 sshd 已不存在={removed}");

            window.NavigateTo(typeof(SshPage));
            await Settle(2500);
            var first = Find<SshPage>(window) ?? throw new InvalidOperationException("找不到 SSH 页");
            var installButton = (System.Windows.Controls.Primitives.ButtonBase)first.FindName("InstallButton");
            bool buttonShown = installButton.IsVisible;
            if (removed)
                installButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); // 真实按钮路径
            else
                _ = SshInstaller.RunAsync(force: true); // 卸载没成功时退化为强制重装，仍可验证切页
            await Settle(500);
            window.NavigateTo(typeof(RemotePage));
            await Settle(800);
            window.NavigateTo(typeof(SshPage));
            await Settle(700);
            var second = Find<SshPage>(window);
            var bar = (System.Windows.Controls.ProgressBar?)second?.FindName("InstallProgress");
            var desc = (System.Windows.Controls.TextBlock?)second?.FindName("InstallDesc");
            var card = (FrameworkElement?)second?.FindName("InstallCard");
            bool running = SshInstaller.IsRunning;
            bool barVisible = bar?.IsVisible == true;
            bool visible = bar?.IsVisible == true && card?.IsVisible == true && desc?.Text == SshInstaller.Stage;
            string stage = desc?.Text ?? "";
            Render(window, Path.Combine(outDir, "ssh-installing.png"), false);

            var until = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < until && (SshInstaller.IsRunning || card?.IsVisible == true)) await Settle(500);
            await Settle(3000);
            bool collapsed = card?.IsVisible == false;
            bool sshRunning = await SshService.IsRunningAsync();
            Render(window, Path.Combine(outDir, "ssh-installed.png"), false);

            bool ok = !ReferenceEquals(first, second) && running && visible && collapsed && SshService.IsInstalled && (!removed || (buttonShown && sshRunning));
            log.AppendLine($"{(ok ? "OK  " : "FAIL")} SSH 安装进度切页保持：新页面实例={!ReferenceEquals(first, second)} 切回时仍在安装={running} 进度条可见={barVisible} 阶段文字=“{stage}” 结束后卡片收起={collapsed} sshd 运行={sshRunning} 安装按钮可见={buttonShown}");
            foreach (var l in SshInstaller.Log) log.AppendLine("     " + l);
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.AppendLine("FAIL SSH 安装进度切页保持：" + ex);
            return 1;
        }
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T t) return t;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find<T>(VisualTreeHelper.GetChild(root, i)) is { } r) return r;
        return null;
    }

    private static readonly Regex Cjk = new(@"[\u4e00-\u9fff]");

    /// <summary>可视树里所有 TextBlock / 按钮文字（ComboBox 下拉项不在可视树里，不检查）。</summary>
    private static List<string> AllTexts(DependencyObject root)
    {
        var list = new List<string>();
        void Walk(DependencyObject d)
        {
            if (d is System.Windows.Controls.TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text)) list.Add(tb.Text);
            else if (d is System.Windows.Controls.TextBox tx && !string.IsNullOrWhiteSpace(tx.Text)) list.Add(tx.Text);
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return list;
    }

    /// <summary>英文界面里残留的中文（语言名「简体中文」除外）。</summary>
    private static List<string> ChineseTexts(DependencyObject root) =>
        AllTexts(root).Where(t => Cjk.IsMatch(t) && t != "简体中文").Distinct().ToList();

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

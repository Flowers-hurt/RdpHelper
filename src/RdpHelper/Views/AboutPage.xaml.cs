using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RdpHelper.Services;
using Wpf.Ui.Controls;

namespace RdpHelper.Views;

public sealed record ThirdParty(string Name, string License, string Usage, string Url)
{
    public string Host => new Uri(Url).Host + new Uri(Url).AbsolutePath.TrimEnd('/');
}

public partial class AboutPage : Page
{
    private ReleaseInfo? _latest;

    // 联网信息只取一次，切页回来复用
    private static Task<List<Contributor>>? _contributors;
    private static Task<(int stars, int forks)>? _stats;

    public AboutPage()
    {
        InitializeComponent();
        VersionPill.Text = "v" + AppPaths.Version;
        ArchPill.Text = $"Windows · {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
        CopyrightText.Text = L.F("Copyright © {0} Flowers-hurt 及贡献者 · 基于 MIT 许可证发布", Math.Max(2026, DateTime.Now.Year));
        AuthorAvatar.Fill = new ImageBrush(GitHubInfo.RemoteImage(GitHubInfo.AuthorAvatar)) { Stretch = Stretch.UniformToFill };

        var parts = new List<ThirdParty>
        {
            new("WPF-UI", "MIT", L.T("Fluent 界面控件库（导航、卡片、毛玻璃窗口）"), "https://github.com/lepoco/wpfui"),
            new(".NET", "MIT", L.T("运行时与 WPF 框架（已内置在程序中）"), "https://github.com/dotnet/runtime"),
            new("Win32-OpenSSH", "OpenSSH License (BSD)", L.T("SSH 服务器，随安装包分发（redist 目录，附许可证）"), "https://github.com/PowerShell/Win32-OpenSSH"),
            new("cloudflared", "Apache-2.0", L.T("Cloudflare 隧道客户端，按需从官方下载"), "https://github.com/cloudflare/cloudflared"),
            new("bore", "MIT", L.T("极简 TCP 穿透客户端，按需从官方下载"), "https://github.com/ekzhang/bore"),
            new("ngrok", L.T("专有软件"), L.T("ngrok 客户端，按需从官方下载，使用需遵守 ngrok 服务条款"), "https://ngrok.com"),
        };
        ThirdPartyList.ItemsSource = parts;
        ThirdPartySummary.Text = L.F("{0} 个组件 · 感谢这些项目让 RdpHelper 成为可能", parts.Count);

        Loaded += async (_, _) => await LoadOnlineAsync();
    }

    private async Task LoadOnlineAsync()
    {
        ContributorState.Text = L.T("正在从 GitHub 加载…");
        _contributors ??= GitHubInfo.ContributorsAsync();
        _stats ??= GitHubInfo.RepoStatsAsync();
        try
        {
            var list = await _contributors;
            ContributorList.ItemsSource = list;
            ContributorState.Text = list.Count == 0 ? L.T("还没有贡献者，期待你成为第一位！") : L.F("共 {0} 位贡献者，感谢每一位！", list.Count);
        }
        catch
        {
            _contributors = null; // 下次进入页面重试
            ContributorState.Text = L.T("暂时无法连接 GitHub，点「查看全部」在浏览器中查看");
        }
        try
        {
            var (stars, forks) = await _stats;
            StatsText.Text = $"★ {stars}  ·  ⑂ {forks}";
            StatsPill.Visibility = Visibility.Visible;
        }
        catch { _stats = null; }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateBusy.Visibility = Visibility.Visible;
        DownloadUpdateButton.Visibility = Visibility.Collapsed;
        try
        {
            _latest = await GitHubInfo.LatestReleaseAsync();
            var current = GitHubInfo.CurrentVersion;
            if (_latest.Version is { } v && v > current)
            {
                UpdateBar.Severity = InfoBarSeverity.Success;
                UpdateBar.Title = L.F("发现新版本 {0}", _latest.Tag);
                UpdateBar.Message = L.F("当前版本 v{0}{1}", AppPaths.Version,
                    _latest.Published is { } d ? L.F("，新版本发布于 {0:yyyy-MM-dd}", d.ToLocalTime()) : "");
                DownloadUpdateButton.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateBar.Severity = InfoBarSeverity.Informational;
                UpdateBar.Title = L.T("已是最新版本");
                UpdateBar.Message = L.F("当前版本 v{0}，最新发布 {1}", AppPaths.Version, _latest.Tag);
            }
        }
        catch (Exception ex)
        {
            UpdateBar.Severity = InfoBarSeverity.Warning;
            UpdateBar.Title = L.T("检查更新失败");
            UpdateBar.Message = ex.Message;
        }
        finally
        {
            UpdateBar.IsOpen = true;
            UpdateButton.IsEnabled = true;
            UpdateBusy.Visibility = Visibility.Collapsed;
        }
    }

    private void DownloadUpdate_Click(object sender, RoutedEventArgs e) => GitHubInfo.Open(_latest?.Url ?? GitHubInfo.ReleasesUrl);

    /// <summary>提 Issue 时附上的环境信息（不含用户名、IP 等隐私）。</summary>
    private void CopyDiag_Click(object sender, RoutedEventArgs e)
    {
        var ed = RdpService.GetEdition();
        var sb = new StringBuilder();
        sb.AppendLine($"- RdpHelper: v{AppPaths.Version} ({RuntimeInformation.ProcessArchitecture})");
        sb.AppendLine($"- OS: {ed.ProductName} {ed.DisplayVersion} (build {ed.Build}, EditionID={ed.EditionId})");
        sb.AppendLine($"- .NET: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"- UI language: {L.Lang} / system {System.Globalization.CultureInfo.InstalledUICulture.Name}");
        sb.AppendLine($"- Remote Desktop: {(RdpService.IsEnabled() ? "on" : "off")}, port {RdpService.GetPort()}, NLA {(RdpService.IsNlaEnabled() ? "on" : "off")}");
        sb.Append($"- OpenSSH installed: {SshService.IsInstalled}, port {SshService.GetPort()}");
        if (RemoteInfo.CopyToClipboard(sb.ToString()))
            Notify.Success(L.T("已复制诊断信息"), L.T("粘贴到 Issue 的「环境信息」一栏即可（不含用户名和 IP）"));
    }

    private void Repo_Click(object sender, RoutedEventArgs e) => GitHubInfo.Open(GitHubInfo.RepoUrl);
    private void Releases_Click(object sender, RoutedEventArgs e) => GitHubInfo.Open(GitHubInfo.ReleasesUrl);
    private void Author_Click(object sender, RoutedEventArgs e) => GitHubInfo.Open(GitHubInfo.AuthorUrl);
    private void Bug_Click(object sender, RoutedEventArgs e) => GitHubInfo.Open(GitHubInfo.NewBugUrl);
    private void Feature_Click(object sender, RoutedEventArgs e) => GitHubInfo.Open(GitHubInfo.NewFeatureUrl);
    private void Contributing_Click(object sender, RoutedEventArgs e) => GitHubInfo.Open(GitHubInfo.ContributingUrl);
    private void Translate_Click(object sender, RoutedEventArgs e) => GitHubInfo.Open(GitHubInfo.RepoUrl + "/blob/main/src/RdpHelper/Localization/en.json");
    private void License_Click(object sender, RoutedEventArgs e) => GitHubInfo.Open(GitHubInfo.LicenseUrl);
    private void Contributors_Click(object sender, RoutedEventArgs e) => GitHubInfo.Open(GitHubInfo.RepoUrl + "/graphs/contributors");

    private void Contributor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url }) GitHubInfo.Open(url);
    }

    private void ThirdParty_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url }) GitHubInfo.Open(url);
    }
}

using System.Text.Json;

namespace RdpHelper.Services;

public sealed record Contributor(string Login, string AvatarUrl, string ProfileUrl, int Contributions)
{
    public string Avatar64 => AvatarUrl + (AvatarUrl.Contains('?') ? "&" : "?") + "s=96";
    public string Tooltip => $"{Login} · {Contributions} commits";

    private System.Windows.Media.ImageSource? _image;

    /// <summary>头像（BitmapImage 会在后台异步下载 http 图片）。需在 UI 线程上访问。</summary>
    public System.Windows.Media.ImageSource Image => _image ??= GitHubInfo.RemoteImage(Avatar64);
}

public sealed record ReleaseInfo(string Tag, string Url, DateTime? Published)
{
    public Version? Version => System.Version.TryParse(Tag.TrimStart('v', 'V').Split('-')[0], out var v) ? v : null;
}

/// <summary>
/// 「关于」页用到的仓库信息：最新版本、贡献者。只用 GitHub 公开 API（匿名、只读），失败时页面照常显示。
/// </summary>
public static class GitHubInfo
{
    public const string Owner = "Flowers-hurt";
    public const string RepoName = "RdpHelper";
    public const string Repo = Owner + "/" + RepoName;
    public const string RepoUrl = "https://github.com/" + Repo;
    public const string AuthorUrl = "https://github.com/" + Owner;
    public const string AuthorAvatar = "https://github.com/" + Owner + ".png?size=160";
    public const string IssuesUrl = RepoUrl + "/issues";
    public const string NewBugUrl = RepoUrl + "/issues/new?template=bug_report.yml";
    public const string NewFeatureUrl = RepoUrl + "/issues/new?template=feature_request.yml";
    public const string ReleasesUrl = RepoUrl + "/releases";
    public const string ContributingUrl = RepoUrl + "/blob/main/CONTRIBUTING.md";
    public const string LicenseUrl = RepoUrl + "/blob/main/LICENSE";
    public const string DiscussionsUrl = RepoUrl + "/discussions";
    public const string StargazersUrl = RepoUrl + "/stargazers";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("RdpHelper/" + AppPaths.Version);
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    public static Version CurrentVersion => System.Version.Parse(AppPaths.Version);

    public static async Task<ReleaseInfo> LatestReleaseAsync()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        var r = doc.RootElement;
        DateTime? published = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTime() : null;
        return new ReleaseInfo(r.GetProperty("tag_name").GetString() ?? "", r.GetProperty("html_url").GetString() ?? ReleasesUrl, published);
    }

    public static async Task<List<Contributor>> ContributorsAsync()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/contributors?per_page=30"));
        return doc.RootElement.EnumerateArray()
            .Where(c => c.GetProperty("type").GetString() == "User")
            .Select(c => new Contributor(
                c.GetProperty("login").GetString() ?? "",
                c.GetProperty("avatar_url").GetString() ?? "",
                c.GetProperty("html_url").GetString() ?? "",
                c.GetProperty("contributions").GetInt32()))
            .ToList();
    }

    /// <summary>仓库 Star / Fork 数。</summary>
    public static async Task<(int stars, int forks)> RepoStatsAsync()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}"));
        var r = doc.RootElement;
        return (r.GetProperty("stargazers_count").GetInt32(), r.GetProperty("forks_count").GetInt32());
    }

    public static System.Windows.Media.ImageSource RemoteImage(string url)
    {
        var img = new System.Windows.Media.Imaging.BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri(url);
        img.DecodePixelWidth = 96;
        img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        img.EndInit();
        return img;
    }

    public static void Open(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
}

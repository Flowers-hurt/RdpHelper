using System.Text.RegularExpressions;

namespace RdpHelper.Services;

/// <summary>
/// OpenSSH 服务器安装任务。状态放在静态类里而不是页面上：切到别的页面再回来，
/// 新页面在 Loaded 时读取 <see cref="Stage"/> / <see cref="Percent"/> / <see cref="Log"/> 继续显示进度。
/// 安装顺序：
///   1. 安装包自带的微软官方 Win32-OpenSSH MSI（redist\OpenSSH-Win64*.msi，离线、十几秒）
///   2. Windows「可选功能」（DISM，从 Windows Update 下载，可解析百分比）
///   3. 从 GitHub 下载 Win32-OpenSSH MSI（显示已下载 MB）
/// </summary>
public static partial class SshInstaller
{
    private static readonly object Gate = new();
    private static Task? _task;
    private static readonly List<string> _log = new();

    public static bool IsRunning { get { lock (Gate) return _task is { IsCompleted: false }; } }
    public static string Stage { get; private set; } = "";

    /// <summary>0–1；小于 0 表示不确定进度（转圈）。</summary>
    public static double Percent { get; private set; } = -1;

    public static string? LastError { get; private set; }

    public static IReadOnlyList<string> Log { get { lock (_log) return _log.ToList(); } }

    /// <summary>状态变化（可能在后台线程触发）。</summary>
    public static event Action? Changed;

    public static event Action<string>? LogLine;

    /// <summary>自带安装包的位置（安装版 / 便携版都放在 exe 旁边的 redist 目录）。</summary>
    public static string? BundledMsi
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "redist");
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "OpenSSH-Win64*.msi").OrderDescending().FirstOrDefault() : null;
        }
    }

    /// <summary>开始安装；已经在装时返回同一个任务。force=true 时即使已安装也用自带 MSI 重装（自检用）。</summary>
    public static Task RunAsync(bool force = false)
    {
        lock (Gate)
        {
            if (_task is { IsCompleted: false }) return _task;
            lock (_log) _log.Clear();
            LastError = null;
            _task = Task.Run(() => CoreAsync(force));
            // 任务真正结束后再通知一次，页面据此收起安装卡片
            _task.ContinueWith(_ => Changed?.Invoke(), TaskScheduler.Default);
            return _task;
        }
    }

    private static async Task CoreAsync(bool force)
    {
        try
        {
            if (SshService.IsInstalled && !force) { Set(L.T("OpenSSH 服务器已安装"), 1); return; }

            // 1) 自带 MSI
            if (BundledMsi is { } msi)
            {
                Set(L.F("正在用自带安装包安装 OpenSSH（{0}）…", Path.GetFileName(msi)), -1);
                if (await InstallMsiAsync(msi)) { Set(L.T("安装完成"), 1); return; }
                Write(L.T("自带安装包安装失败，改用 Windows 可选功能"));
            }
            else
            {
                Write(L.T("没有找到自带的 OpenSSH 安装包，改用 Windows 可选功能"));
            }

            // 2) Windows 可选功能（DISM）
            Set(L.T("正在查询 Windows 可选功能…"), -1);
            var q = await Shell.PowerShellAsync("(Get-WindowsCapability -Online -ErrorAction Stop | Where-Object Name -like 'OpenSSH.Server*' | Select-Object -First 1).Name", 5 * 60_000);
            var cap = q.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("OpenSSH.Server"));
            if (cap != null)
            {
                Set(L.T("正在通过 Windows 可选功能安装（从 Windows Update 下载）…"), 0);
                var r = await Shell.RunAsync(Path.Combine(Environment.SystemDirectory, "dism.exe"),
                    $"/Online /Add-Capability /CapabilityName:{cap} /NoRestart /English", 30 * 60_000, null, OnDismLine);
                Write($"DISM exit {r.ExitCode}");
                if (SshService.IsInstalled) { Set(L.T("安装完成"), 1); return; }
                Write(Tail(r.Output));
            }
            else
            {
                Write(L.T("系统里找不到 OpenSSH.Server 可选功能") + " " + Tail(q.Output));
            }

            // 3) 在线下载 MSI
            await DownloadAndInstallAsync();
            if (!SshService.IsInstalled) throw new InvalidOperationException(L.T("OpenSSH 安装失败，详见安装日志"));
            Set(L.T("安装完成"), 1);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Write(ex.Message);
            Set(L.F("安装失败：{0}", ex.Message), -1);
            throw;
        }
    }

    private static async Task<bool> InstallMsiAsync(string msi)
    {
        var logFile = Path.Combine(Path.GetTempPath(), "RdpHelper-OpenSSH-msi.log");
        Write($"> msiexec /i \"{msi}\" /qn /norestart");
        var r = await Shell.RunAsync("msiexec.exe", $"/i \"{msi}\" /qn /norestart /l*v \"{logFile}\"", 10 * 60_000);
        Write($"msiexec exit {r.ExitCode}" + (r.ExitCode is 0 or 3010 ? "" : " · log: " + logFile));
        return r.ExitCode is 0 or 3010 && SshService.IsInstalled;
    }

    private static async Task DownloadAndInstallAsync()
    {
        Set(L.T("正在查询 Win32-OpenSSH 最新版本…"), -1);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RdpHelper/" + AppPaths.Version);
        var json = await http.GetStringAsync("https://api.github.com/repos/PowerShell/Win32-OpenSSH/releases/latest");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        string? url = doc.RootElement.GetProperty("assets").EnumerateArray()
            .Select(a => a.GetProperty("browser_download_url").GetString())
            .FirstOrDefault(u => u != null && u.Contains("Win64", StringComparison.OrdinalIgnoreCase) && u.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
        if (url == null) throw new InvalidOperationException(L.T("没有找到 Win32-OpenSSH 的 MSI 安装包"));
        Write(L.F("下载：{0}", url));

        string msi = Path.Combine(Path.GetTempPath(), "OpenSSH-Win64.msi");
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(msi);
            var buf = new byte[81920];
            long done = 0;
            int n;
            var last = DateTime.MinValue;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if ((DateTime.UtcNow - last).TotalMilliseconds > 150)
                {
                    last = DateTime.UtcNow;
                    Set(total > 0
                            ? L.F("正在下载 Win32-OpenSSH 安装包… {0:0.0} / {1:0.0} MB", done / 1048576.0, total.Value / 1048576.0)
                            : L.F("正在下载 Win32-OpenSSH 安装包… {0:0.0} MB", done / 1048576.0),
                        total > 0 ? (double)done / total.Value : -1);
                }
            }
        }
        Write(L.F("已下载 {0:0.0} MB", new FileInfo(msi).Length / 1048576.0));
        Set(L.F("正在安装 {0} …", Path.GetFileName(url)), -1);
        await InstallMsiAsync(msi);
    }

    [GeneratedRegex(@"(\d{1,3}(?:\.\d)?)%")]
    private static partial Regex PercentRegex();

    private static void OnDismLine(string line)
    {
        var m = PercentRegex().Match(line);
        if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p))
        {
            Set(L.F("正在通过 Windows 可选功能安装… {0:0}%", p), Math.Clamp(p / 100, 0, 1));
            return;
        }
        if (!string.IsNullOrWhiteSpace(line)) Write(line.Trim());
    }

    private static string Tail(string s) => string.Join(" ", s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(4));

    private static void Set(string stage, double percent)
    {
        bool stageChanged = stage != Stage;
        Stage = stage;
        Percent = percent;
        if (stageChanged && !stage.Contains('%') && !stage.Contains(" MB")) Write(stage);
        Changed?.Invoke();
    }

    private static void Write(string line)
    {
        line = $"[{DateTime.Now:HH:mm:ss}] {line}";
        lock (_log) { _log.Add(line); if (_log.Count > 500) _log.RemoveAt(0); }
        LogLine?.Invoke(line);
    }
}

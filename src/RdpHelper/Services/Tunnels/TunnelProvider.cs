using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace RdpHelper.Services.Tunnels;

/// <summary>
/// 内网穿透组件的统一抽象：下载 / 导入 → 启动子进程 → 从输出里解析公网地址 → 停止。
/// 组件不打进安装包，首次使用时从官方地址下载到 %LOCALAPPDATA%\RdpHelper\tools\&lt;Id&gt;\。
/// </summary>
public abstract partial class TunnelProvider
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Summary { get; }
    public abstract string Homepage { get; }
    public abstract string ExeName { get; }

    /// <summary>注意事项（账号要求、访问方需要做什么等）。</summary>
    public abstract string Notes { get; }

    public virtual bool NeedsToken => false;
    public virtual string TokenHint => "";

    public string ToolDir => Path.Combine(AppPaths.Tools, Id);
    public string ExePath => Path.Combine(ToolDir, ExeName);
    public bool IsInstalled => File.Exists(ExePath);

    public string? PublicAddress { get; private set; }
    public bool IsRunning => _proc is { HasExited: false };
    public int? LocalPort { get; private set; }

    public event Action<string>? Log;
    public event Action? StateChanged;

    private Process? _proc;
    private readonly List<string> _recent = new();

    // ---------------- 安装 ----------------

    /// <summary>返回下载地址（可能需要先查询 GitHub API）。</summary>
    protected abstract Task<string> ResolveDownloadUrlAsync(HttpClient http, CancellationToken ct);

    public async Task InstallAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ToolDir);
        using var http = CreateHttp();
        string url = await ResolveDownloadUrlAsync(http, ct);
        Emit("下载：" + url);
        string tmp = Path.Combine(ToolDir, "download.tmp");
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buf = new byte[81920];
            long done = 0;
            int n;
            var lastReport = DateTime.MinValue;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0 && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                {
                    progress?.Report((double)done / total.Value);
                    lastReport = DateTime.UtcNow;
                }
            }
        }
        progress?.Report(1);

        if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using (var zip = ZipFile.OpenRead(tmp))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException("压缩包里没找到 " + ExeName);
                entry.ExtractToFile(ExePath, true);
            }
            File.Delete(tmp);
        }
        else
        {
            File.Move(tmp, ExePath, true);
        }
        Emit("已安装到 " + ExePath);
        StateChanged?.Invoke();
    }

    /// <summary>网络不好下载失败时，可以手动下载后导入。</summary>
    public void ImportExe(string path)
    {
        Directory.CreateDirectory(ToolDir);
        File.Copy(path, ExePath, true);
        Emit("已导入 " + path);
        StateChanged?.Invoke();
    }

    public void Uninstall()
    {
        Stop();
        try { if (Directory.Exists(ToolDir)) Directory.Delete(ToolDir, true); } catch { /* 占用 */ }
        StateChanged?.Invoke();
    }

    public async Task<string> GetVersionAsync()
    {
        if (!IsInstalled) return "";
        var r = await Shell.RunAsync(ExePath, VersionArgs, 15_000, ToolDir);
        return r.Output.Split('\n').FirstOrDefault()?.Trim() ?? "";
    }

    protected virtual string VersionArgs => "--version";

    // ---------------- 运行 ----------------

    protected abstract string BuildArgs(int port, RemoteKind target);

    /// <summary>从一行输出中解析公网地址；解析不到返回 null。</summary>
    protected abstract string? ParseAddress(string line);

    public RemoteKind Target { get; private set; }

    public async Task<string> StartAsync(int port, RemoteKind target = RemoteKind.Rdp, TimeSpan? timeout = null)
    {
        if (!IsInstalled) throw new InvalidOperationException(Name + " 尚未下载");
        Stop();
        PublicAddress = null;
        LocalPort = port;
        Target = target;
        lock (_recent) _recent.Clear();

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var psi = new ProcessStartInfo(ExePath, BuildArgs(port, target))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = ToolDir,
        };
        psi.Environment["NO_COLOR"] = "1";
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void OnLine(string? raw)
        {
            if (raw == null) return;
            var line = StripAnsi(raw);
            if (line.Length == 0) return;
            lock (_recent) { _recent.Add(line); if (_recent.Count > 30) _recent.RemoveAt(0); }
            Emit(line);
            if (PublicAddress == null && ParseAddress(line) is { } addr)
            {
                PublicAddress = addr;
                tcs.TrySetResult(addr);
                StateChanged?.Invoke();
            }
        }
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.Exited += (_, _) =>
        {
            Emit($"进程已退出（代码 {SafeExitCode(p)}）");
            string tail;
            lock (_recent) tail = string.Join("\n", _recent.TakeLast(6));
            tcs.TrySetException(new InvalidOperationException(Name + " 启动失败：\n" + tail));
            if (ReferenceEquals(_proc, p))
            {
                _proc = null;
                PublicAddress = null;
            }
            StateChanged?.Invoke();
        };
        Emit($"> {ExeName} {BuildArgs(port, target)}");
        p.Start();
        ChildProcessJob.Add(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _proc = p;
        StateChanged?.Invoke();

        var wait = timeout ?? TimeSpan.FromSeconds(60);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(wait));
        if (done != tcs.Task)
        {
            Stop();
            throw new TimeoutException($"{Name} 在 {wait.TotalSeconds:0} 秒内没有返回公网地址，可能是网络无法连接到服务商。");
        }
        return await tcs.Task;
    }

    public void Stop()
    {
        var p = _proc;
        _proc = null;
        if (p != null)
        {
            try { if (!p.HasExited) p.Kill(true); } catch { /* ignore */ }
            p.Dispose();
            Emit("已停止");
        }
        PublicAddress = null;
        StateChanged?.Invoke();
    }

    // ---------------- 给访问方的说明 ----------------

    /// <summary>访问方如何用这个公网地址连进来。</summary>
    public abstract string VisitorGuide(string address, RemoteKind target);

    // ---------------- 工具 ----------------

    protected void Emit(string line) => Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {line}");

    protected static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RdpHelper/" + AppPaths.Version);
        return http;
    }

    /// <summary>从 GitHub 最新 Release 里挑选资产下载地址。</summary>
    protected static async Task<string> GitHubAssetAsync(HttpClient http, string repo, Func<string, bool> match, CancellationToken ct)
    {
        var json = await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (match(name)) return a.GetProperty("browser_download_url").GetString()!;
        }
        throw new InvalidOperationException($"在 {repo} 最新版本里没有找到 Windows 安装包");
    }

    private static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -1; } }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiRegex();

    protected static string StripAnsi(string s) => AnsiRegex().Replace(s, "").Trim();
}

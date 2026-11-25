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
    public bool IsInstalled => File.Exists(ExePath) || TryUseBundled();

    /// <summary>随安装包内置的组件文件名（放在程序目录 redist\ 下）；null 表示不内置、需要下载。</summary>
    protected virtual string? BundledFileName => null;

    public string? BundledPath => BundledFileName is { } f ? Path.Combine(AppContext.BaseDirectory, "redist", f) : null;

    /// <summary>安装包里带了这个组件（bore 约 1 MB，内置后断网 / 连不上 GitHub 也能直接用）。</summary>
    public bool IsBundled => BundledPath is { } b && File.Exists(b);

    /// <summary>组件目录里还没有时，把内置的那份拷过去（之后「重新下载 / 更新」或导入的版本会覆盖它）。</summary>
    private bool TryUseBundled()
    {
        if (!IsBundled) return false;
        try
        {
            Directory.CreateDirectory(ToolDir);
            File.Copy(BundledPath!, ExePath, true);
            return true;
        }
        catch { return false; }
    }

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

    /// <summary>下载状态放在 provider（全局单例）上，切换页面回来卡片仍能显示进度。</summary>
    public bool IsDownloading { get; private set; }

    /// <summary>0–1；小于 0 表示大小未知。</summary>
    public double DownloadFraction { get; private set; } = -1;

    public string DownloadText { get; private set; } = "";

    public async Task InstallAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        if (IsDownloading) throw new InvalidOperationException(L.F("{0} 正在下载中", Name));
        IsDownloading = true;
        DownloadFraction = -1;
        DownloadText = "";
        StateChanged?.Invoke();
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                try { await InstallCoreAsync(progress, ct); break; }
                catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested && IsTransient(ex))
                {
                    Emit(L.F("下载出错，{0} 秒后重试（第 {1} 次）：{2}", attempt * 2, attempt + 1, ex.Message));
                    await Task.Delay(attempt * 2000, ct);
                }
            }
        }
        catch (Exception ex)
        {
            var msg = ExplainNetworkError(ex);
            Emit(L.F("下载失败：{0}", msg));
            throw new InvalidOperationException(msg, ex);
        }
        finally
        {
            IsDownloading = false;
            StateChanged?.Invoke();
        }
    }

    private async Task InstallCoreAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolDir);
        using var http = CreateHttp();
        string url = await ResolveDownloadUrlAsync(http, ct);
        Emit(L.F("下载：{0}", url));
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
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 150)
                {
                    DownloadFraction = total > 0 ? (double)done / total.Value : -1;
                    DownloadText = total > 0
                        ? L.F("已下载 {0:0.0} / {1:0.0} MB", done / 1048576.0, total.Value / 1048576.0)
                        : L.F("已下载 {0:0.0} MB", done / 1048576.0);
                    if (total > 0) progress?.Report(DownloadFraction);
                    lastReport = DateTime.UtcNow;
                    StateChanged?.Invoke();
                }
            }
        }
        progress?.Report(1);

        if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using (var zip = ZipFile.OpenRead(tmp))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException(L.F("压缩包里没找到 {0}", ExeName));
                entry.ExtractToFile(ExePath, true);
            }
            File.Delete(tmp);
        }
        else
        {
            File.Move(tmp, ExePath, true);
        }
        Emit(L.F("已安装到 {0}", ExePath));
        StateChanged?.Invoke();
    }

    /// <summary>网络不好下载失败时，可以手动下载后导入。</summary>
    public void ImportExe(string path)
    {
        Directory.CreateDirectory(ToolDir);
        File.Copy(path, ExePath, true);
        Emit(L.F("已导入 {0}", path));
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

    /// <summary>根据进程最后几行输出给出人能看懂的失败原因；null 表示没有特别说明。</summary>
    protected virtual string? ExplainStartFailure(string tail) => null;

    /// <summary>从一行输出中解析公网地址；解析不到返回 null。</summary>
    protected abstract string? ParseAddress(string line);

    public RemoteKind Target { get; private set; }

    public async Task<string> StartAsync(int port, RemoteKind target = RemoteKind.Rdp, TimeSpan? timeout = null)
    {
        if (!IsInstalled) throw new InvalidOperationException(L.F("{0} 尚未下载", Name));
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
            Emit(L.F("进程已退出（代码 {0}）", SafeExitCode(p)));
            string tail;
            lock (_recent) tail = string.Join("\n", _recent.TakeLast(6));
            tcs.TrySetException(new InvalidOperationException(L.F("{0} 启动失败：", Name) + "\n" + (ExplainStartFailure(tail) is { } hint ? hint + "\n\n" : "") + tail));
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
            throw new TimeoutException(L.F("{0} 在 {1:0} 秒内没有返回公网地址，可能是网络无法连接到服务商。", Name, wait.TotalSeconds));
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
            Emit(L.T("已停止"));
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
        throw new InvalidOperationException(L.F("在 {0} 最新版本里没有找到 Windows 安装包", repo));
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or IOException or TaskCanceledException { InnerException: TimeoutException };

    /// <summary>把底层网络错误（常见是系统语言的原始报错，如「由于目标计算机积极拒绝」）说成人话，并给出办法。</summary>
    public static string ExplainNetworkError(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null && inner is not System.Net.Sockets.SocketException) inner = inner.InnerException;
        string why = inner switch
        {
            System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused } =>
                L.T("连接被拒绝：本机网络或代理拦截了下载地址（常见于公司网络、校园网，或开着代理软件但代理没在运行）。"),
            System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData } =>
                L.T("域名解析失败：请检查网络或 DNS 设置。"),
            System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.TimedOut } or TimeoutException or TaskCanceledException =>
                L.T("连接超时：访问 GitHub 等国外地址很慢或被拦截。"),
            System.Security.Authentication.AuthenticationException =>
                L.T("安全连接失败：可能被网络中间设备或代理拦截。"),
            HttpRequestException { StatusCode: { } code } =>
                L.F("服务器返回 {0}，可能是下载地址暂时不可用或访问太频繁，稍后再试。", (int)code),
            _ => ex.Message,
        };
        return why + " " + L.T("可以换个网络 / 开代理后重试，或用浏览器手动下载后点「导入本地 exe」。");
    }

    private static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -1; } }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiRegex();

    protected static string StripAnsi(string s) => AnsiRegex().Replace(s, "").Trim();
}

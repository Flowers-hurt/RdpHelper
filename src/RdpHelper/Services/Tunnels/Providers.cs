using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace RdpHelper.Services.Tunnels;

/// <summary>Cloudflare Quick Tunnel：免注册、免费；访问方也需要 cloudflared（或本程序）做本地转发。</summary>
public sealed partial class CloudflareProvider : TunnelProvider
{
    public override string Id => "cloudflared";
    public override string Name => "Cloudflare Tunnel";
    public override string Summary => L.T("免注册、完全免费，走 Cloudflare 全球网络，不暴露端口。访问方需同样运行 cloudflared（或本程序）。");
    public override string Homepage => "https://github.com/cloudflare/cloudflared";
    public override string ExeName => "cloudflared.exe";
    public override string Notes =>
        L.T("使用 TryCloudflare 临时隧道，每次启动会分配一个随机的 *.trycloudflare.com 域名，关闭即失效。\n") +
        L.T("RDP 是 TCP 协议，访问方不能直接用 mstsc 连这个域名，需要先在自己电脑上运行 cloudflared 做本地转发——") +
        L.T("如果对方也装了 RdpHelper，在「内网穿透 → 连接别人的电脑」里粘贴地址即可一键连接。");

    protected override Task<string> ResolveDownloadUrlAsync(HttpClient http, CancellationToken ct) =>
        Task.FromResult("https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe");

    protected override string BuildArgs(int port, RemoteKind target) =>
        $"tunnel --no-autoupdate --url {(target == RemoteKind.Ssh ? "ssh" : "rdp")}://localhost:{port}";

    [GeneratedRegex(@"https://([a-z0-9-]+\.trycloudflare\.com)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    protected override string? ParseAddress(string line)
    {
        var m = UrlRegex().Match(line);
        return m.Success && !m.Groups[1].Value.StartsWith("api.", StringComparison.OrdinalIgnoreCase) ? m.Groups[1].Value : null;
    }

    public override string VisitorGuide(string address, RemoteKind target) => target == RemoteKind.Ssh
        ? L.T("访问方式（Cloudflare 隧道 · SSH）：对方安装 cloudflared 后运行：") + "\n" +
          $"    ssh -o ProxyCommand=\"cloudflared access ssh --hostname {address}\" {Environment.UserName}@{address}"
        : L.T("访问方式（Cloudflare 隧道）：") + "\n" +
          "  " + L.F("方式一：对方安装 RdpHelper → 内网穿透 → 连接别人的电脑 → 粘贴 {0} → 连接", address) + "\n" +
          "  " + L.T("方式二：对方下载 cloudflared（https://github.com/cloudflare/cloudflared/releases），在命令行运行：") + "\n" +
          $"    cloudflared access rdp --hostname {address} --url rdp://localhost:13389\n" +
          "    " + L.T("保持窗口不关，然后 Win+R 运行 mstsc，计算机填 localhost:13389");

    // ---------- 访问方：把对方的隧道映射到本机端口，再拉起 mstsc ----------

    private Process? _access;
    public bool IsAccessRunning => _access is { HasExited: false };

    public async Task<int> StartAccessAsync(string hostname, int timeoutMs = 20_000)
    {
        if (!IsInstalled) throw new InvalidOperationException(L.T("请先下载 cloudflared"));
        hostname = NormalizeHost(hostname);
        StopAccess();
        int localPort = FreeLocalPort();
        var psi = new ProcessStartInfo(ExePath, $"access rdp --hostname {hostname} --url rdp://localhost:{localPort}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = ToolDir,
        };
        var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Emit(L.T("[访问]") + " " + StripAnsi(e.Data)); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Emit(L.T("[访问]") + " " + StripAnsi(e.Data)); };
        Emit($"> cloudflared access rdp --hostname {hostname} --url rdp://localhost:{localPort}");
        p.Start();
        ChildProcessJob.Add(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _access = p;

        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (p.HasExited) throw new InvalidOperationException(L.T("cloudflared 访问端已退出，请检查地址是否正确"));
            if (RdpService.IsListening(localPort)) return localPort;
            await Task.Delay(300);
        }
        throw new TimeoutException(L.T("本地转发端口没有就绪"));
    }

    public void StopAccess()
    {
        var p = _access;
        _access = null;
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(true); } catch { /* ignore */ }
        p.Dispose();
    }

    public static string NormalizeHost(string s)
    {
        s = s.Trim();
        foreach (var prefix in new[] { "https://", "http://", "rdp://", "tcp://" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        return s.TrimEnd('/');
    }

    private static int FreeLocalPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

/// <summary>ngrok：老牌穿透，需要免费账号的 authtoken；TCP 隧道需要账号完成银行卡验证。</summary>
public sealed partial class NgrokProvider : TunnelProvider
{
    public override string Id => "ngrok";
    public override string Name => "ngrok";
    public override string Summary => L.T("老牌穿透服务，得到 tcp://x.tcp.ngrok.io:端口，对方直接用 mstsc 连接。需要 ngrok 账号 Authtoken。");
    public override string Homepage => "https://ngrok.com";
    public override string ExeName => "ngrok.exe";
    public override bool NeedsToken => true;
    public override string TokenHint => L.T("在 https://dashboard.ngrok.com/get-started/your-authtoken 复制 Authtoken");
    public override string Notes =>
        L.T("需要注册 ngrok 账号并填入 Authtoken（保存在 ngrok 自己的配置文件里，本程序不留存）。\n") +
        L.T("注意：ngrok 免费账号开启 TCP 隧道需先在后台完成银行卡验证（不扣费），否则会报 ERR_NGROK_8013。");

    protected override Task<string> ResolveDownloadUrlAsync(HttpClient http, CancellationToken ct) =>
        Task.FromResult("https://bin.equinox.io/c/bNyj1mQVY4c/ngrok-v3-stable-windows-amd64.zip");

    protected override string VersionArgs => "version";

    protected override string BuildArgs(int port, RemoteKind target) => $"tcp {port} --log stdout --log-format logfmt";

    [GeneratedRegex(@"url=tcp://(\S+)")]
    private static partial Regex UrlRegex();

    protected override string? ParseAddress(string line)
    {
        var m = UrlRegex().Match(line);
        return m.Success ? m.Groups[1].Value.Trim('"') : null;
    }

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ngrok", "ngrok.yml");

    public bool HasToken
    {
        get
        {
            try { return File.Exists(ConfigPath) && File.ReadAllText(ConfigPath).Contains("authtoken"); }
            catch { return false; }
        }
    }

    public async Task SaveTokenAsync(string token)
    {
        token = token.Trim();
        if (token.Length < 10) throw new ArgumentException(L.T("Authtoken 看起来不对"));
        var r = await Shell.RunAsync(ExePath, $"config add-authtoken {token}", 20_000, ToolDir);
        if (!r.Ok) throw new InvalidOperationException(r.Output);
        Emit(L.T("Authtoken 已保存到 ngrok 配置"));
    }

    public override string VisitorGuide(string address, RemoteKind target) => target == RemoteKind.Ssh
        ? L.F("访问方式（ngrok · SSH）：{0}", SshVia(address))
        : L.F("访问方式（ngrok）：Win+R 运行 mstsc，计算机填 {0}", address);

    internal static string SshVia(string address)
    {
        var i = address.LastIndexOf(':');
        return i > 0 ? $"ssh {Environment.UserName}@{address[..i]} -p {address[(i + 1)..]}" : $"ssh {Environment.UserName}@{address}";
    }
}

/// <summary>bore：开源极简 TCP 穿透，使用公共服务器 bore.pub，免注册。</summary>
public sealed partial class BoreProvider : TunnelProvider
{
    public override string Id => "bore";
    public override string Name => "bore";
    public override string Summary => L.T("开源极简 TCP 穿透（Rust），用公共服务器 bore.pub，免注册，对方直接用 mstsc 连接。");
    public override string Homepage => "https://github.com/ekzhang/bore";
    public override string ExeName => "bore.exe";
    protected override string? BundledFileName => "bore.exe";
    public override string Notes =>
        L.T("组件已随安装包内置，无需下载。使用 bore 作者提供的公共中转 bore.pub（端口 7835），免费但不保证可用性和速度，适合临时应急。\n") +
        L.T("得到的 bore.pub:端口 任何人都能访问，请务必给 Windows 账户设置强密码，用完立即停止。");

    protected override Task<string> ResolveDownloadUrlAsync(HttpClient http, CancellationToken ct) =>
        GitHubAssetAsync(http, "ekzhang/bore",
            n => n.Contains("x86_64-pc-windows", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase), ct);

    protected override string BuildArgs(int port, RemoteKind target) => $"local {port} --to bore.pub";

    protected override string? ExplainStartFailure(string tail) =>
        tail.Contains("could not connect to bore.pub", StringComparison.OrdinalIgnoreCase)
            ? L.T("连不上 bore.pub 公共服务器：本机网络拦截了它的 7835 端口，或服务器暂时不可用。可以换个网络（如手机热点）再试，或在「更多服务商」里改用 Cloudflare Tunnel。")
            : null;

    [GeneratedRegex(@"listening at ([\w\.\-]+:\d+)")]
    private static partial Regex ListenRegex();

    [GeneratedRegex(@"remote_port=(\d+)")]
    private static partial Regex PortRegex();

    protected override string? ParseAddress(string line)
    {
        var m = ListenRegex().Match(line);
        if (m.Success) return m.Groups[1].Value;
        var p = PortRegex().Match(line);
        return p.Success ? "bore.pub:" + p.Groups[1].Value : null;
    }

    public override string VisitorGuide(string address, RemoteKind target) => target == RemoteKind.Ssh
        ? L.F("访问方式（bore · SSH）：{0}", NgrokProvider.SshVia(address))
        : L.F("访问方式（bore）：Win+R 运行 mstsc，计算机填 {0}", address);
}

public static class TunnelManager
{
    public static CloudflareProvider Cloudflare { get; } = new();
    public static NgrokProvider Ngrok { get; } = new();
    public static BoreProvider Bore { get; } = new();

    public static IReadOnlyList<TunnelProvider> All { get; } = new TunnelProvider[] { Cloudflare, Ngrok, Bore };

    public static void StopAll()
    {
        foreach (var p in All) p.Stop();
        Cloudflare.StopAccess();
    }
}

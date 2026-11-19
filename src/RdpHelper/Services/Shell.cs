using System.Diagnostics;
using System.Text;

namespace RdpHelper.Services;

/// <summary>隐藏窗口执行外部命令 / PowerShell 脚本。</summary>
public static class Shell
{
    public sealed record Result(int ExitCode, string Output)
    {
        public bool Ok => ExitCode == 0;
    }

    public static async Task<Result> RunAsync(string file, string args, int timeoutMs = 60_000, string? workDir = null, Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDir ?? Environment.CurrentDirectory,
        };
        using var p = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (sb) sb.AppendLine(e.Data); onLine?.Invoke(e.Data); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (sb) sb.AppendLine(e.Data); onLine?.Invoke(e.Data); } };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* ignore */ }
            return new Result(-1, sb + "\n" + L.F("[超时] 命令执行超过 {0} 秒", timeoutMs / 1000));
        }
        p.WaitForExit(); // 等待异步输出读完
        return new Result(p.ExitCode, sb.ToString().Trim());
    }

    /// <summary>PowerShell 脚本里写的是中文提示前缀（WARN:xxx：），在这里换成当前界面语言。</summary>
    private static readonly string[] PsWarnPrefixes =
    {
        L.N("启动远程桌面服务失败："), L.N("重启远程桌面服务失败："), L.N("启动 sshd 失败："),
        L.N("停止 sshd 失败："), L.N("重启 sshd 失败："), L.N("创建防火墙规则失败："),
    };

    /// <summary>取出输出中所有 WARN: 行并翻译前缀。</summary>
    public static List<string> Warnings(string output) => output.Split('\n')
        .Select(l => l.Trim()).Where(l => l.StartsWith("WARN:")).Select(l => TranslateWarn(l[5..])).ToList();

    private static string TranslateWarn(string line)
    {
        foreach (var p in PsWarnPrefixes)
            if (line.StartsWith(p)) return L.T(p) + line[p.Length..];
        return line;
    }

    /// <summary>以 -EncodedCommand 方式执行 PowerShell，避免任何引号转义问题；输出统一为 UTF-8。</summary>
    public static Task<Result> PowerShellAsync(string script, int timeoutMs = 90_000)
    {
        var full = "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;\n" + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(ps)) ps = "powershell.exe";
        return RunAsync(ps, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, timeoutMs);
    }
}

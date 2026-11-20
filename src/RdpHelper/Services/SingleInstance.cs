using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RdpHelper.Services;

/// <summary>
/// 单实例：再次双击图标时不弹「已经在运行」，而是通知已运行的实例把窗口显示出来；
/// 如果已运行的实例没有响应（窗口已关但进程残留），结束它并正常启动。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "Global\\RdpHelper.SingleInstance";
    private const string ShowName = "Global\\RdpHelper.ShowWindow";
    private const string AckName = "Global\\RdpHelper.ShowWindowAck";

    private static Mutex? _mutex;
    private static EventWaitHandle? _show, _ack;
    private static RegisteredWaitHandle? _wait;

    /// <summary>抢到单实例锁返回 true（本进程继续启动）；false 表示已唤醒另一个实例，本进程应退出。</summary>
    public static bool Acquire(Action<string> log)
    {
        if (TryTake()) return true;

        // 1) 新版实例：发「显示窗口」信号，等它在界面线程里确认
        if (EventWaitHandle.TryOpenExisting(ShowName, out var show) && EventWaitHandle.TryOpenExisting(AckName, out var ack))
        {
            using (show) using (ack)
            {
                ack.Reset();
                show.Set();
                if (ack.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    log("activated running instance");
                    return false;
                }
            }
            log("running instance did not respond in 5s");
        }

        // 2) 旧版或无响应的实例：窗口还看得见就切到前台；看不见说明进程残留，结束它
        foreach (var p in Others())
        {
            using (p)
            {
                try
                {
                    p.Refresh();
                    if (p.MainWindowHandle != IntPtr.Zero && p.Responding)
                    {
                        if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(p.MainWindowHandle);
                        log($"switched to running window pid={p.Id}");
                        return false;
                    }
                    log($"killing windowless leftover pid={p.Id}");
                    p.Kill(true);
                    p.WaitForExit(5000);
                }
                catch (Exception ex) { log($"handling pid={p.Id} failed: {ex.Message}"); }
            }
        }
        return TryTake();
    }

    private static bool TryTake()
    {
        _mutex ??= new Mutex(false, MutexName);
        try
        {
            var others = Others();
            bool alone = others.Length == 0;
            foreach (var p in others) p.Dispose();
            // 没有别的进程时多等一会儿：上一个实例可能正在退出、还没来得及释放锁
            if (!_mutex.WaitOne(TimeSpan.FromSeconds(alone ? 3 : 0))) return false;
        }
        catch (AbandonedMutexException) { /* 上一个实例被结束，锁已归我们 */ }
        return true;
    }

    private static Process[] Others()
    {
        int self = Environment.ProcessId;
        return Process.GetProcessesByName("RdpHelper").Where(p =>
        {
            if (p.Id != self) return true;
            p.Dispose();
            return false;
        }).ToArray();
    }

    /// <summary>主实例：监听「显示窗口」信号，在界面线程执行 <paramref name="show"/> 后回执。</summary>
    public static void Listen(Action show)
    {
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        _ack = new EventWaitHandle(false, EventResetMode.AutoReset, AckName);
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) =>
        {
            dispatcher.InvokeAsync(() =>
            {
                show();
                _ack?.Set();
            });
        }, null, Timeout.Infinite, false);
    }

    public static void Release()
    {
        _wait?.Unregister(null);
        _show?.Dispose();
        _ack?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { /* 未持有 */ }
        _mutex?.Dispose();
    }

    private const int SW_RESTORE = 9;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
}

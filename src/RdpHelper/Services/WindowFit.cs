using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RdpHelper.Services;

/// <summary>
/// 让主窗口打开时一定完整地落在屏幕内并居中：
///   1. SourceInitialized（还没显示）时，按鼠标所在显示器的「工作区」（扣掉任务栏）缩小尺寸并居中；
///   2. 显示后再用真实像素矩形复核一次，跨不同 DPI 显示器时如仍有超出则直接按像素纠正。
/// 小屏笔记本（如 1366×768 + 125% 缩放，工作区约 1093×580）上不会再出现标题栏跑到屏幕外。
/// </summary>
public static class WindowFit
{
    /// <summary>窗口最多占工作区的比例，四周留一点边。</summary>
    private const double MaxFraction = 0.94;

    public static void FitAndCenter(Window w)
    {
        GetCursorPos(out var pt);
        var mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var wa = WorkArea(mon);
        var dpi = VisualTreeHelper.GetDpi(w);
        double left = wa.Left / dpi.DpiScaleX, top = wa.Top / dpi.DpiScaleY;
        double width = (wa.Right - wa.Left) / dpi.DpiScaleX, height = (wa.Bottom - wa.Top) / dpi.DpiScaleY;

        double maxW = Math.Floor(width * MaxFraction), maxH = Math.Floor(height * MaxFraction);
        w.MinWidth = Math.Min(w.MinWidth, maxW);
        w.MinHeight = Math.Min(w.MinHeight, maxH);
        w.Width = Math.Min(double.IsNaN(w.Width) ? maxW : w.Width, maxW);
        w.Height = Math.Min(double.IsNaN(w.Height) ? maxH : w.Height, maxH);
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = Math.Round(left + (width - w.Width) / 2);
        w.Top = Math.Round(top + (height - w.Height) / 2);
    }

    /// <summary>显示后复核：窗口像素矩形必须完整位于所在显示器工作区内，否则缩小并居中。</summary>
    public static void EnsureInside(Window w)
    {
        if (w.WindowState != WindowState.Normal) return;
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
        var wa = WorkArea(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
        if (r.Left >= wa.Left && r.Top >= wa.Top && r.Right <= wa.Right && r.Bottom <= wa.Bottom) return;

        int waW = wa.Right - wa.Left, waH = wa.Bottom - wa.Top;
        int ww = Math.Min(r.Right - r.Left, (int)(waW * MaxFraction));
        int wh = Math.Min(r.Bottom - r.Top, (int)(waH * MaxFraction));
        SetWindowPos(hwnd, IntPtr.Zero, wa.Left + (waW - ww) / 2, wa.Top + (waH - wh) / 2, ww, wh, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>自检用：窗口是否完整在工作区内，以及说明文字。</summary>
    public static (bool inside, string detail) Check(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        GetWindowRect(hwnd, out var r);
        var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var wa = WorkArea(mon);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref mi);
        int cx0 = (r.Left + r.Right) / 2 - (wa.Left + wa.Right) / 2, cy0 = (r.Top + r.Bottom) / 2 - (wa.Top + wa.Bottom) / 2;
        bool inside = r.Left >= wa.Left && r.Top >= wa.Top && r.Right <= wa.Right && r.Bottom <= wa.Bottom
                      && Math.Abs(cx0) <= 16 && Math.Abs(cy0) <= 16;
        int cx = (r.Left + r.Right) / 2 - (wa.Left + wa.Right) / 2, cy = (r.Top + r.Bottom) / 2 - (wa.Top + wa.Bottom) / 2;
        var dpi = VisualTreeHelper.GetDpi(w);
        return (inside, $"screen {mi.rcMonitor.Right - mi.rcMonitor.Left}x{mi.rcMonitor.Bottom - mi.rcMonitor.Top}px @ {dpi.PixelsPerInchX:0}dpi, " +
                        $"work area ({wa.Left},{wa.Top})-({wa.Right},{wa.Bottom}), window ({r.Left},{r.Top})-({r.Right},{r.Bottom}), off-center ({cx},{cy})px");
    }

    private static RECT WorkArea(IntPtr monitor)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref mi)) return mi.rcWork;
        var a = SystemParameters.WorkArea;
        return new RECT { Left = (int)a.Left, Top = (int)a.Top, Right = (int)a.Right, Bottom = (int)a.Bottom };
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

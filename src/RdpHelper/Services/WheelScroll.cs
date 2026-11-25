using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace RdpHelper.Services;

/// <summary>
/// 整个窗口统一处理鼠标滚轮：从鼠标下的元素往外找，交给第一个「这个方向还能滚」的 ScrollViewer。
/// WPF 默认是最里层的 ScrollViewer 吃掉滚轮——鼠标停在只读文本框（连接信息预览、运行日志）、
/// 列表、卡片里的控件上时，即使它们自己已经滚到头或根本不能滚，整页也滚不动。
/// </summary>
public static class WheelScroll
{
    /// <summary>每格滚轮滚动的像素（与系统「一次滚动 3 行」接近）。</summary>
    private const double PixelsPerNotch = 56;

    public static void Attach(Window window) => window.PreviewMouseWheel += (_, e) => Handle(window, e);

    /// <summary>自检用：模拟一格滚轮落在 element 上，返回是否被某个 ScrollViewer 处理。</summary>
    public static bool Simulate(Window window, DependencyObject element, int delta)
    {
        var e = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
            Source = element,
        };
        Handle(window, e, element);
        return e.Handled;
    }

    private static void Handle(Window window, MouseWheelEventArgs e, DependencyObject? origin = null)
    {
        if (e.Handled || e.Delta == 0) return;
        if ((origin ?? e.OriginalSource) is not DependencyObject start) return;
        // 下拉框展开等弹出层里的滚动交给它自己
        if (start is Visual v && PresentationSource.FromVisual(v) is { } src && !ReferenceEquals(src, PresentationSource.FromVisual(window))) return;

        bool up = e.Delta > 0;
        for (var cur = start; cur != null; cur = Parent(cur))
        {
            if (cur is ComboBox { IsDropDownOpen: true }) return;
            if (cur is Popup) return;
            if (cur is not ScrollViewer sv || sv.ScrollableHeight <= 0.5) continue;
            if (sv.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) continue;
            bool canMove = up ? sv.VerticalOffset > 0.5 : sv.VerticalOffset < sv.ScrollableHeight - 0.5;
            if (!canMove) continue;

            int notches = Math.Max(1, Math.Abs(e.Delta) / 120);
            if (sv.CanContentScroll)
            {
                for (int i = 0; i < notches * 3; i++) { if (up) sv.LineUp(); else sv.LineDown(); }
            }
            else
            {
                double target = sv.VerticalOffset + (up ? -1 : 1) * notches * PixelsPerNotch;
                sv.ScrollToVerticalOffset(Math.Clamp(target, 0, sv.ScrollableHeight));
            }
            e.Handled = true;
            return;
        }
    }

    private static DependencyObject? Parent(DependencyObject d)
    {
        if (d is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            var p = VisualTreeHelper.GetParent(d);
            if (p != null) return p;
        }
        return LogicalTreeHelper.GetParent(d);
    }
}

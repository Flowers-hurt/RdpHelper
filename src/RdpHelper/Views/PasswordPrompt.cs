using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace RdpHelper.Views;

/// <summary>输入两次新密码的小对话框（重置远程账户密码用）。</summary>
public static class PasswordPrompt
{
    public static string? Ask(Window? owner, string title)
    {
        var p1 = new Wpf.Ui.Controls.PasswordBox { PlaceholderText = L.T("新密码"), Margin = new Thickness(0, 12, 0, 8) };
        var p2 = new Wpf.Ui.Controls.PasswordBox { PlaceholderText = L.T("再输入一次"), Margin = new Thickness(0, 0, 0, 8) };
        var error = new System.Windows.Controls.TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        var ok = new Wpf.Ui.Controls.Button { Content = L.T("确定"), Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), IsDefault = true, MinWidth = 90 };
        var cancel = new Wpf.Ui.Controls.Button { Content = L.T("取消"), IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(24, 16, 24, 20) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(p1);
        panel.Children.Add(p2);
        panel.Children.Add(error);
        panel.Children.Add(buttons);

        var w = new FluentWindow
        {
            Owner = owner,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ExtendsContentIntoTitleBar = true,
            WindowBackdropType = WindowBackdropType.Mica,
            ShowInTaskbar = false,
            Title = title,
        };
        var root = new StackPanel();
        root.Children.Add(new TitleBar { Title = "RdpHelper", ShowMaximize = false, ShowMinimize = false });
        root.Children.Add(panel);
        w.Content = root;

        string? result = null;
        ok.Click += (_, _) =>
        {
            if (p1.Password.Length == 0) { error.Text = L.T("密码不能为空"); return; }
            if (p1.Password != p2.Password) { error.Text = L.T("两次输入的密码不一致"); return; }
            result = p1.Password;
            w.DialogResult = true;
        };
        w.Loaded += (_, _) => p1.Focus();
        return w.ShowDialog() == true ? result : null;
    }
}

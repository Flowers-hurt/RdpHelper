using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using RdpHelper.Services;

namespace RdpHelper.Views;

public partial class SettingsPage : Page
{
    public const string RepoUrl = GitHubInfo.RepoUrl;
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        var s = AppSettings.Current;
        Select(ThemeBox, s.Theme);
        Select(BackdropBox, s.Backdrop);
        Select(LanguageBox, s.Language);
        ToolsPathText.Text = AppPaths.Tools;
        SettingsPathText.Text = AppPaths.SettingsFile;
        VersionText.Text = L.F("版本 {0} · MIT 开源 · 基于 .NET 8 + WPF-UI", AppPaths.Version);
        _loading = false;
    }

    private static void Select(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if ((string)item.Tag == tag) { box.SelectedItem = item; return; }
        box.SelectedIndex = 0;
    }

    private void Appearance_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        s.Theme = (string)((ComboBoxItem)ThemeBox.SelectedItem).Tag;
        s.Backdrop = (string)((ComboBoxItem)BackdropBox.SelectedItem).Tag;
        s.Save();
        if (Application.Current.MainWindow is MainWindow w) App.ApplyAppearance(w);
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        var tag = (string)((ComboBoxItem)LanguageBox.SelectedItem).Tag;
        if (tag == s.Language) return;
        s.Language = tag;
        s.Save();
        string before = L.Lang;
        L.Apply(tag);
        if (L.Lang != before) Dispatcher.BeginInvoke(App.ReloadUi);
    }

    private void OpenTools_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Tools);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Tools}\"") { UseShellExecute = true });
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow w) w.NavigateTo(typeof(AboutPage));
    }

    private void Github_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true });
}

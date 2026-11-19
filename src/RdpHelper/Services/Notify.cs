using Wpf.Ui;
using Wpf.Ui.Controls;

namespace RdpHelper.Services;

/// <summary>右下角 Snackbar 提示。</summary>
public static class Notify
{
    private static SnackbarService? _svc;

    public static void Init(SnackbarPresenter presenter)
    {
        _svc = new SnackbarService();
        _svc.SetSnackbarPresenter(presenter);
    }

    public static void Success(string title, string message = "") =>
        Show(title, message, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);

    public static void Info(string title, string message = "") =>
        Show(title, message, ControlAppearance.Info, SymbolRegular.Info24);

    public static void Warn(string title, string message = "") =>
        Show(title, message, ControlAppearance.Caution, SymbolRegular.Warning24, 7);

    public static void Error(string title, string message = "") =>
        Show(title, message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24, 8);

    private static void Show(string title, string message, ControlAppearance appearance, SymbolRegular icon, int seconds = 4)
    {
        if (_svc == null) return;
        _svc.Show(title, string.IsNullOrEmpty(message) ? " " : message, appearance, new SymbolIcon(icon), TimeSpan.FromSeconds(seconds));
    }
}

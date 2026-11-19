using System.Windows;
using RdpHelper.Services;
using RdpHelper.Views;
using Wpf.Ui.Controls;

namespace RdpHelper;

public partial class MainWindow : FluentWindow
{
    public MainWindow(Type? startPage = null)
    {
        InitializeComponent();
        App.ApplyAppearance(this);
        Notify.Init(SnackbarPresenter);
        Loaded += (_, _) => RootNavigation.Navigate(startPage ?? typeof(RemotePage));
    }

    public bool NavigateTo(Type pageType) => RootNavigation.Navigate(pageType);
}

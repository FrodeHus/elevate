using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Elevate.App.Views;

/// <summary>The flyout's content. The shell version shows the header, a notice and the footer.</summary>
public sealed partial class PanelView : UserControl
{
    private AppModel? _model;
    private FlyoutWindow? _window;

    public PanelView()
    {
        InitializeComponent();
    }

    public void Bind(AppModel model, FlyoutWindow window)
    {
        _model = model;
        _window = window;
        model.Changed += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Redraws everything from the model. Cheap enough to run on every change.</summary>
    public void Refresh()
    {
        if (_model is null)
        {
            return;
        }

        OfflinePill.Visibility = _model.IsOnline ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.IsEnabled = _model.IsOnline;
        if (_model.Notice is { } notice)
        {
            NoticeBar.Message = notice;
            NoticeBar.IsOpen = true;
        }
        else
        {
            NoticeBar.IsOpen = false;
        }

        EmptyText.Text = _model.Identities.Count == 0
            ? (_model.IsConfigured ? "Add an account to see your PIM roles." : "Complete initial setup: enter your app registration's client ID in Settings, or add an account with the Azure CLI app.")
            : $"{_model.Identities.Count} account(s) signed in.";
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            _ = _model.RefreshAllAsync(userInitiated: true);
        }
    }

    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (_model is not null)
        {
            _model.Notice = null;
        }
    }

    private void OnAddAccount(object sender, RoutedEventArgs e) => App.Current.OpenAddAccount();

    private void OnSettings(object sender, RoutedEventArgs e) => App.Current.OpenSettings();

    private void OnQuit(object sender, RoutedEventArgs e) => App.Current.Quit();
}

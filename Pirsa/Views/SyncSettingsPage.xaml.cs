using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.ViewModels;

namespace Pirsa.Views;

/// <summary>
/// Settings → Sync. Connects one or more watched-state providers and reports
/// the last sync for each. Provider cards are rendered from their descriptor,
/// so a new provider does not need new XAML.
/// </summary>
public sealed partial class SyncSettingsPage : Page
{
    public SyncSettingsViewModel ViewModel { get; } = new(App.Sync);

    public SyncSettingsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Detach();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SyncProviderRowViewModel row)
        {
            await ViewModel.ConnectAsync(row);
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SyncProviderRowViewModel row)
        {
            await ViewModel.DisconnectAsync(row);
        }
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SyncNowAsync();
    }

    private void CancelConnect_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelConnect();
    }

    private async void OpenAuthUrl_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SyncProviderRowViewModel row &&
            Uri.TryCreate(row.AuthUrl, UriKind.Absolute, out var uri))
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    private void ProviderPassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SyncProviderRowViewModel row &&
            sender is PasswordBox passwordBox)
        {
            row.Password = passwordBox.Password;
        }
    }
}

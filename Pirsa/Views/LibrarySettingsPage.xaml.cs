using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.ViewModels;

namespace Pirsa.Views;

/// <summary>Settings → Library &amp; data. Local history, Home rows, data folder.</summary>
public sealed partial class LibrarySettingsPage : Page
{
    public LibrarySettingsViewModel ViewModel { get; } = new(
        App.Addons,
        App.WatchProgress,
        App.SearchHistory);

    public LibrarySettingsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Attach();
        await ViewModel.InitializeAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Detach();
    }

    private void ManageHome_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(HomeSettingsPage));
    }

    private void Notice_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissNotice();

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => ViewModel.OpenDataFolder();

    private async void ClearCache_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.ClearCacheAsync();

    private void OpenLog_Click(object sender, RoutedEventArgs e) => ViewModel.OpenPlayerLog();

    private async void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Reset all settings?",
            Content = "This restores every preference on the Settings pages: playback, subtitles, appearance, startup and updates. Add-ons, watch history, the watchlist and sync connections stay as they are.",
            PrimaryButtonText = "Reset settings",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ResetAllAsync();
        }
    }

    private async void ClearWatchHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear watch history?",
            Content = "This forgets every saved movie and episode position, every watched mark, the last working source for each title, and any subtitle timing on this device. Your watchlist, add-ons and settings stay as they are.",
            PrimaryButtonText = "Clear history",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ClearWatchHistoryAsync();
        }
    }

    private async void ClearSearchHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear search history?",
            Content = "This removes every saved search suggestion on this device. Your watch history, watchlist and add-ons stay as they are.",
            PrimaryButtonText = "Clear searches",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ClearSearchHistoryAsync();
        }
    }
}

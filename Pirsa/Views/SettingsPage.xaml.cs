using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.Helpers;
using Pirsa.Services;
using Pirsa.ViewModels;
using Windows.System;

namespace Pirsa.Views;

/// <summary>
/// Settings → Addons. The only settings surface for now; more can be added
/// here without touching navigation.
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; } = new(
        App.Addons,
        App.Settings,
        App.WatchProgress,
        App.SearchHistory);

    public SettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>App version from the build, e.g. "v0.1.0". Works packed or unpacked.</summary>
    public string AppVersion => AppInfo.VersionText;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Attach();

        // Apply a hand-off message before the addon list finishes loading, so
        // the notice is already visible when the page renders.
        if (e.Parameter is SettingsMessage message)
        {
            if (message.IsError)
            {
                ViewModel.ShowError(message.Text);
            }
            else
            {
                ViewModel.ShowNotice(message.Text);
            }
        }

        await ViewModel.InitializeAsync();
        QualityPanel.Visibility = ViewModel.AutoplayEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Detach();
    }

    private void AutoplayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        QualityPanel.Visibility = AutoplayToggle.IsOn
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.AddAsync();
        if (!ViewModel.HasError)
        {
            UrlBox.Focus(FocusState.Programmatic);
        }
    }

    private async void UrlBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await ViewModel.AddAsync();
    }

    private void HomeSettings_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(HomeSettingsPage));
    }

    private async void AddonEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AddonEntry entry ||
            sender is not ToggleSwitch toggle ||
            toggle.IsOn == entry.IsEnabled)
        {
            return;
        }

        await ViewModel.SetEnabledAsync(entry, toggle.IsOn);
    }

    private async void MoveAddonUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AddonEntry entry)
        {
            await ViewModel.MoveAsync(entry, -1);
        }
    }

    private async void MoveAddonDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AddonEntry entry)
        {
            await ViewModel.MoveAsync(entry, 1);
        }
    }

    private void AddonFilter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag && int.TryParse(tag, out var index))
        {
            ViewModel.FilterIndex = index;
        }
    }

    private void ClearAddonFilters_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearFilters();

        if (AddonFilterBar.Items.Count > 0)
        {
            AddonFilterBar.SelectedItem = AddonFilterBar.Items[0];
        }
    }

    private void Notice_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissNotice();

    private async void RemoveAddon_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AddonEntry entry)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Remove addon?",
            Content = $"Remove \"{entry.Name}\"? Its catalogs and stream sources will no longer appear in Pirsa.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.RemoveAsync(entry);
        }
    }

    private async void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Restore default addon?",
            Content = "This removes your configured addons and restores Cinemeta and OpenSubtitles v3. You can add the others again later.",
            PrimaryButtonText = "Restore defaults",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.RestoreDefaultsAsync();
        }
    }

    private async void ClearWatchHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear watch history?",
            Content = "This forgets every saved movie and episode position, and every watched mark, on this device. Your watchlist, addons and settings stay as they are.",
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
            Content = "This removes every saved search suggestion on this device. Your watch history, watchlist and addons stay as they are.",
            PrimaryButtonText = "Clear searches",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ClearSearchHistoryAsync();
        }
    }

    private async void RetryAddon_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AddonEntry entry)
        {
            await ViewModel.RetryAsync(entry);
        }
    }
}

/// <summary>
/// A notice or error to show once Settings opens, used when an addon link
/// installs an addon, or fails to, without closing the current page first.
/// </summary>
public sealed record SettingsMessage(string Text, bool IsError);

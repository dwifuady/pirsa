using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
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
    public SettingsViewModel ViewModel { get; } = new(App.Addons, App.Settings);

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
        QualityPanel.Visibility = ViewModel.AutoplayEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
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

    private async void RetryAddon_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AddonEntry entry)
        {
            await ViewModel.RetryAsync(entry);
        }
    }
}

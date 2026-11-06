using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.Services;
using Pirsa.ViewModels;

namespace Pirsa.Views;

/// <summary>Settings → Add-ons. Add, enable, reorder and remove Stremio add-ons.</summary>
public sealed partial class AddonsSettingsPage : Page
{
    public AddonsSettingsViewModel ViewModel { get; } = new(App.Addons);

    public AddonsSettingsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Attach();

        if (e.Parameter is SettingsMessage message)
        {
            ShowMessage(message);
        }

        await ViewModel.InitializeAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Detach();
    }

    /// <summary>Shows a hand-off from an addon link without leaving the page.</summary>
    public void ShowMessage(SettingsMessage message)
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

    private async void AddAddon_Click(object sender, RoutedEventArgs e)
    {
        var urlBox = new TextBox
        {
            Header = "Manifest URL or add-on link",
            PlaceholderText = "https://example.com/manifest.json or stremio://example.com/manifest.json",
            Text = ViewModel.NewManifestUrl,
        };

        var note = new TextBlock
        {
            Text = "Pirsa reads the manifest, shows what the add-on provides, and asks before installing. Only add add-ons you trust and are allowed to use.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.72,
        };

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(urlBox);
        content.Children.Add(note);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Add add-on",
            Content = content,
            PrimaryButtonText = "Add add-on",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ViewModel.NewManifestUrl = urlBox.Text;
        await ViewModel.AddAsync();
    }

    private void Notice_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissNotice();

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

    private static AddonEntry? AddonFrom(object sender) =>
        (sender as FrameworkElement)?.Tag as AddonEntry ??
        (sender as FrameworkElement)?.DataContext as AddonEntry;

    private async void MoveAddonUp_Click(object sender, RoutedEventArgs e)
    {
        if (AddonFrom(sender) is { } entry)
        {
            await ViewModel.MoveAsync(entry, -1);
        }
    }

    private async void MoveAddonDown_Click(object sender, RoutedEventArgs e)
    {
        if (AddonFrom(sender) is { } entry)
        {
            await ViewModel.MoveAsync(entry, 1);
        }
    }

    private async void AddonList_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        if (!ViewModel.CanReorder ||
            args.Items.Count == 0 ||
            args.Items[0] is not AddonEntry entry)
        {
            return;
        }

        var index = ViewModel.VisibleAddons.IndexOf(entry);
        if (index >= 0)
        {
            await ViewModel.MoveToAsync(entry, index);
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

    private async void RemoveAddon_Click(object sender, RoutedEventArgs e)
    {
        if (AddonFrom(sender) is not { } entry)
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
            Title = "Restore default add-ons?",
            Content = "This removes your configured add-ons and restores Cinemeta and OpenSubtitles v3. You can add the others again later.",
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

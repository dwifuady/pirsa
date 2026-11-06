using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.ViewModels;

namespace Pirsa.Views;

/// <summary>Settings → Subtitles. Preferred language and saved timing.</summary>
public sealed partial class SubtitlesSettingsPage : Page
{
    public SubtitlesSettingsViewModel ViewModel { get; } = new(
        App.Settings,
        App.SubtitleOffsets,
        App.SubtitleChoices);

    public SubtitlesSettingsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
        UpdateSubtitlePreview();
    }

    private void Notice_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissNotice();

    private void SubtitleAppearance_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSubtitlePreview();

    private void UpdateSubtitlePreview()
    {
        if (SubtitleCaptionBackground is null)
        {
            return;
        }

        SubtitleCaptionBackground.Background = ViewModel.SubtitleBackgroundIndex switch
        {
            0 => new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            2 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0)),
            _ => new SolidColorBrush(Windows.UI.Color.FromArgb(140, 0, 0, 0)),
        };
    }

    private async void ClearTiming_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear saved subtitle timing?",
            Content = "This forgets every timing adjustment and per-title language choice on this device. Your preferred language stays as it is.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ClearSavedAsync();
        }
    }
}

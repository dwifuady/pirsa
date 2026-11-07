using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.ViewModels;

namespace Pirsa.Views;

/// <summary>Settings → Playback. Autoplay, quality, episode chaining, engine.</summary>
public sealed partial class PlaybackSettingsPage : Page
{
    public PlaybackSettingsViewModel ViewModel { get; } = new(App.Settings);

    public PlaybackSettingsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
        UpdateSkipOptionsVisibility();
    }

    private void SkipSegmentsToggle_Toggled(object sender, RoutedEventArgs e) =>
        UpdateSkipOptionsVisibility();

    private void UpdateSkipOptionsVisibility()
    {
        SkipOptionsPanel.Visibility = SkipSegmentsToggle.IsOn
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}

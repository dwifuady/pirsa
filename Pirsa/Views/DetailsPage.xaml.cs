using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.Helpers;
using Pirsa.Models;
using Pirsa.ViewModels;

namespace Pirsa.Views;

/// <summary>
/// Movie / series details with a poster-to-hero connected animation.
/// </summary>
public sealed partial class DetailsPage : Page
{
    private const string PosterAnimationKey = "PosterAnimation";

    private readonly DispatcherTimer _warmTimer;
    private ConnectedAnimation? _posterAnimation;
    private PlaybackRequest? _warmRequest;

    public DetailsViewModel ViewModel { get; } = new(App.Addons, App.WatchProgress, App.Watchlist);

    public DetailsPage()
    {
        InitializeComponent();

        // Hovering across episode rows shouldn't fire a fetch for each one.
        _warmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _warmTimer.Tick += (_, _) => FlushWarmRequest();

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not MetaItem preview)
        {
            ViewModel.HasError = true;
            ViewModel.ErrorMessage = "This title is missing its details.";
            return;
        }

        // Show the catalog preview immediately, then refresh with full meta.
        ViewModel.Attach();
        ViewModel.Meta = preview;
        PreparePosterAnimation();

        await ViewModel.LoadAsync(preview.Type, preview.Id);

        // Warm stream discovery for the episode Play would start, so pressing
        // Play shows sources (or starts playback) without waiting on addons.
        WarmPlaybackTarget();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        _posterAnimation = null;
        _warmTimer.Stop();
        _warmRequest = null;
        ViewModel.Detach();
    }

    private void PreparePosterAnimation()
    {
        _posterAnimation = ConnectedAnimationService
            .GetForCurrentView()
            .GetAnimation(PosterAnimationKey);

        if (_posterAnimation is null)
        {
            return;
        }

        _posterAnimation.Configuration = new BasicConnectedAnimationConfiguration();

        if (PosterImage.IsLoaded)
        {
            _posterAnimation.TryStart(PosterImage);
            _posterAnimation = null;
        }
        else
        {
            PosterImage.Loaded += PosterImage_Loaded;
        }
    }

    private void PosterImage_Loaded(object sender, RoutedEventArgs e)
    {
        PosterImage.Loaded -= PosterImage_Loaded;
        _posterAnimation?.TryStart(PosterImage);
        _posterAnimation = null;
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (BuildPlayRequest() is not { } request)
        {
            return;
        }

        if (!await StreamSetupPrompt.EnsurePlayableAsync(XamlRoot, OpenAddonSettings))
        {
            return;
        }

        Frame.Navigate(
            typeof(PlayerPage),
            request,
            new DrillInNavigationTransitionInfo());
    }

    private async void Episode_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Meta is not { } series ||
            ((FrameworkElement)sender).DataContext is not MetaVideo episode)
        {
            return;
        }

        if (!await StreamSetupPrompt.EnsurePlayableAsync(XamlRoot, OpenAddonSettings))
        {
            return;
        }

        Frame.Navigate(
            typeof(PlayerPage),
            PlaybackRequest.ForEpisode(series, episode),
            new DrillInNavigationTransitionInfo());
    }

    private void OpenAddonSettings() => Frame.Navigate(typeof(SettingsPage));

    private void ManageAddons_Click(object sender, RoutedEventArgs e) => OpenAddonSettings();

    /// <summary>
    /// The request the Play button uses: continue a saved movie or episode when
    /// there is one, otherwise the first episode of the selected season.
    /// </summary>
    private PlaybackRequest? BuildPlayRequest()
    {
        if (ViewModel.Meta is not { } meta)
        {
            return null;
        }

        return ViewModel.ResumeEntry is { } resume
            ? resume.ToPlaybackRequest()
            : PlaybackRequest.ForMeta(meta, ViewModel.SelectedSeason?.Episodes.FirstOrDefault());
    }

    private void WarmPlaybackTarget()
    {
        // Nothing to warm when no addon can produce a stream.
        if (ViewModel.StreamAddonsKnown && !ViewModel.HasEnabledStreamAddon)
        {
            return;
        }

        if (BuildPlayRequest() is { } request)
        {
            App.StreamDiscovery.Warm(request);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DetailsViewModel.SelectedSeason) ||
            ViewModel.Meta is not { } meta)
        {
            return;
        }

        // The Play button now targets this season's first episode.
        if (ViewModel.SelectedSeason?.Episodes.FirstOrDefault() is { } first)
        {
            App.StreamDiscovery.Warm(PlaybackRequest.ForEpisode(meta, first));
        }
    }

    private void Episode_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.Meta is not { } meta ||
            (sender as FrameworkElement)?.DataContext is not MetaVideo episode)
        {
            return;
        }

        _warmRequest = PlaybackRequest.ForEpisode(meta, episode);
        _warmTimer.Stop();
        _warmTimer.Start();
    }

    private void FlushWarmRequest()
    {
        _warmTimer.Stop();

        if (_warmRequest is not { } request)
        {
            return;
        }

        _warmRequest = null;
        App.StreamDiscovery.Warm(request);
    }

    private async void Imdb_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(ViewModel.ImdbUrl, UriKind.Absolute, out var uri))
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }
}

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.Helpers;
using Pirsa.Models;
using Pirsa.ViewModels;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Pirsa.Views;

/// <summary>
/// Movie / series details with a poster-to-hero connected animation.
/// </summary>
public sealed partial class DetailsPage : Page
{
    private const string PosterAnimationKey = "PosterAnimation";

    private readonly DispatcherTimer _warmTimer;
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private ConnectedAnimation? _posterAnimation;
    private PlaybackRequest? _warmRequest;
    private Storyboard? _backdropDrift;
    private bool _backdropDriftBegun;
    private bool _backdropDriftPaused;
    private bool _heroVisible = true;

    public DetailsViewModel ViewModel { get; } = new(App.Addons, App.WatchProgress, App.Watchlist);

    public DetailsPage()
    {
        InitializeComponent();

        // Hovering across episode rows shouldn't fire a fetch for each one.
        _warmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _warmTimer.Tick += (_, _) => FlushWarmRequest();

        _backdropDrift = Resources["BackdropDriftStoryboard"] as Storyboard;
        if (_backdropDrift is not null)
        {
            // Bind the animations to the transform here instead of by name:
            // a Storyboard inside a ResourceDictionary does not resolve
            // TargetName against the page's namescope.
            foreach (var animation in _backdropDrift.Children)
            {
                Storyboard.SetTarget(animation, BackdropDrift);
            }
        }

        Loaded += DetailsPage_Loaded;

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
        StopBackdropDrift();
        ViewModel.Detach();
    }

    private void DetailsPage_Loaded(object sender, RoutedEventArgs e) => StartBackdropDrift();

    private void BackdropHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The drift scales the backdrop past its own bounds; without this the
        // overscan would paint over the facts and episodes below the hero.
        BackdropHost.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
        };
    }

    private void Hero_EffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        _heroVisible = args.EffectiveViewport is { Width: > 0, Height: > 0 };

        if (_heroVisible)
        {
            StartBackdropDrift();
        }
        else
        {
            PauseBackdropDrift();
        }
    }

    /// <summary>
    /// Starts the backdrop drift once the hero is on screen and the image is
    /// there. Decorative motion stays off when Windows is asked to reduce it
    /// or when a High Contrast theme is active.
    /// </summary>
    private void StartBackdropDrift()
    {
        if (_backdropDrift is null ||
            !_heroVisible ||
            ViewModel.BackdropImage is null ||
            !_uiSettings.AnimationsEnabled ||
            _accessibility.HighContrast)
        {
            return;
        }

        if (!_backdropDriftBegun)
        {
            _backdropDriftBegun = true;
            _backdropDriftPaused = false;
            _backdropDrift.Begin();
            return;
        }

        if (_backdropDriftPaused)
        {
            _backdropDriftPaused = false;
            _backdropDrift.Resume();
        }
    }

    private void PauseBackdropDrift()
    {
        if (_backdropDrift is null || !_backdropDriftBegun || _backdropDriftPaused)
        {
            return;
        }

        _backdropDriftPaused = true;
        _backdropDrift.Pause();
    }

    private void StopBackdropDrift()
    {
        if (_backdropDrift is null || !_backdropDriftBegun)
        {
            return;
        }

        _backdropDriftBegun = false;
        _backdropDriftPaused = false;
        _backdropDrift.Stop();
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

        // In selection mode the row toggles its checkbox instead of playing.
        if (ViewModel.IsSelectingEpisodes)
        {
            episode.IsSelected = !episode.IsSelected;
            ViewModel.NotifySelectionChanged();
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
        // The preview meta arrives before the full one; start on whichever
        // first brings a backdrop.
        if (e.PropertyName == nameof(DetailsViewModel.BackdropImage))
        {
            StartBackdropDrift();
            return;
        }

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

    private void EpisodeSelection_Click(object sender, RoutedEventArgs e) =>
        ViewModel.NotifySelectionChanged();

    /// <summary>
    /// Right-click menu on an episode row: mark this episode watched or
    /// unwatched, or add it to a bulk selection.
    /// </summary>
    private void Episode_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (ViewModel.Meta is null ||
            sender is not FrameworkElement element ||
            element.DataContext is not MetaVideo episode)
        {
            return;
        }

        args.Handled = true;

        var watched = ViewModel.IsEpisodeWatched(episode);
        var toggle = new MenuFlyoutItem
        {
            Text = watched ? "Mark as unwatched" : "Mark as watched",
            Icon = new FontIcon { Glyph = watched ? "" : "" },
        };
        toggle.Click += async (_, _) => await ViewModel.SetEpisodeWatchedAsync(episode, !watched);

        var select = new MenuFlyoutItem
        {
            Text = episode.IsSelected ? "Remove from selection" : "Add to selection",
            Icon = new FontIcon { Glyph = episode.IsSelected ? "" : "" },
        };
        select.Click += (_, _) =>
        {
            episode.IsSelected = !episode.IsSelected;
            ViewModel.NotifySelectionChanged();
        };

        var flyout = new MenuFlyout();
        flyout.Items.Add(toggle);
        flyout.Items.Add(select);

        if (args.TryGetPosition(element, out var position))
        {
            flyout.ShowAt(element, position);
        }
        else
        {
            flyout.ShowAt(element);
        }
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

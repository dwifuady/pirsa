using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.Helpers;
using Pirsa.Models;
using Pirsa.Playback;
using Pirsa.Services;
using Pirsa.ViewModels;
using Windows.Foundation;
using Windows.System;
using Windows.Storage;
using Windows.UI.ViewManagement;

namespace Pirsa.Views;

/// <summary>
/// Native-feeling video player with WinUI chrome. Playback goes through
/// <see cref="IPlaybackEngine"/>; libVLC is the current backend and handles the
/// container/codec variety these addons throw at it (MKV, HEVC, HDR, HLS,
/// DASH).
/// </summary>
public sealed partial class PlayerPage : Page
{
    private static readonly HttpClient SubtitleHttp = CreateSubtitleHttpClient();

    /// <summary>Volume points changed per mouse-wheel detent.</summary>
    private const double WheelVolumeStep = 5;

    /// <summary>One tap of the timing control, fine enough to land on most offsets.</summary>
    private const long SubtitleDelayStepMs = 100;

    /// <summary>Beyond a minute the subtitle file belongs to a different release.</summary>
    private const long SubtitleDelayMaxMs = 60_000;

    /// <summary>Room kept between lifted subtitles and the control bar.</summary>
    private const double SubtitleLiftGapDip = 12;

    /// <summary>
    /// How long the playhead may stand still before the buffering overlay shows.
    /// Engines only emit time updates a few times a second, and a healthy seek
    /// pauses decoding for a fraction of that, so this clears normal seeks.
    /// </summary>
    private static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// The last seconds of a title are not watched through a spinner. The engine
    /// stops advancing the clock while the final buffered frames drain, so the playhead
    /// looks frozen at ~99% even though the picture is fine.
    /// </summary>
    private const long EndOfTitleGraceMs = 4000;

    private readonly PlayerViewModel _viewModel = new(
        App.Addons,
        App.StreamDiscovery,
        App.WatchProgress);
    private readonly DispatcherTimer _chromeTimer;
    private readonly DispatcherTimer _stallTimer;
    private readonly Stopwatch _seekWatch = new();
    private bool _bufferingLogged;
    private bool _stallOverlayVisible;
    private long _advancePositionMs = -1;
    private DateTime _advanceUtc = DateTime.UtcNow;
    private string _bufferText = "Buffering…";

    private readonly IPlaybackEngine _engine;
    private CancellationTokenSource? _sourceCts;
    private CancellationTokenSource? _subtitleCts;
    private PlaybackSource? _pendingSource;
    private PlaybackSource? _currentSource;
    private SubtitleVersionChoice? _selectedSubtitle;
    private SubtitleLanguageChoice? _versionsLanguage;
    private string? _selectedSubtitlePath;
    private string? _subtitleDirectory;
    private long _selectedEmbeddedTrackId = -1;
    private string? _selectedEmbeddedName;
    private string? _selectedEmbeddedCode;
    private long _subtitleDelayMs;
    private DispatcherTimer? _subtitleHudTimer;

    private bool _disposed;
    private bool _playing;
    private bool _ended;
    private bool _switching;
    private bool _errorVisible;
    private bool _sourcesFlyoutOpen;
    private bool _sourcesFlyoutPending;
    private bool _syncingSourceTabs;
    private bool _subtitlesFlyoutOpen;
    private bool _episodesFlyoutOpen;
    private bool _subtitleApplied;
    private bool _suppressSubtitleSelection;
    private bool _draggingSeek;
    private bool _suppressSeek;
    private bool _suppressVolume;
    private IReadOnlyList<PlaybackTrack> _audioTracks = [];
    private long _selectedAudioTrackId = -1;
    private bool _audioListDirty = true;
    private bool _chromeHidden = true;
    private int _appliedSubtitleLift = -1;
    private bool _keyboardActive;
    private bool _isFullScreen;
    private long _resumeMs;
    private long _resumeFromHistoryMs;
    private long _positionMs;
    private long _lengthMs;
    private DateTime _lastProgressSaveUtc;
    private DispatcherTimer? _resumeHintTimer;
    private DispatcherTimer? _cachedWatchdog;
    private PlaybackSource? _cachedAttempt;
    private StreamCacheSession? _streamCache;
    private readonly CancellationTokenSource _streamCacheCts = new();
    private int _playAttempt;
    private Task<bool>? _sourceDiscovery;
    private CancellationTokenSource? _nextEpisodeCts;
    private MetaItem? _seriesMeta;
    private MetaVideo? _nextEpisode;
    private bool _upNextDismissed;
    private bool _advancing;
    private int _lastVolume = 80;

    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private Storyboard? _loadingBreath;

    public PlayerViewModel ViewModel => _viewModel;

    public PlayerPage()
    {
        InitializeComponent();

        _engine = PlaybackEngines.Create();
        VideoHost.Children.Add(_engine.Surface);
        HookEngineEvents();

        ConfigureLoadingBreath();

        _chromeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _chromeTimer.Tick += (_, _) =>
        {
            _chromeTimer.Stop();
            HideChrome();
        };

        _resumeHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _resumeHintTimer.Tick += (_, _) => HideResumeHint();

        _subtitleHudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        _subtitleHudTimer.Tick += (_, _) =>
        {
            _subtitleHudTimer?.Stop();
            SubtitleHudPanel.Visibility = Visibility.Collapsed;
        };

        // A remembered source that never reaches "playing" is treated as dead.
        _cachedWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _cachedWatchdog.Tick += (_, _) =>
        {
            _cachedWatchdog?.Stop();
            if (!_playing)
            {
                _ = HandleCachedSourceFailureAsync();
            }
        };

        // The engine's buffering percentage is useless as a UI signal: libVLC
        // reports "Playing" with a frozen picture while it climbs from 0 to 100
        // over several seconds, and keeps reporting low percentages after a
        // seek that cost nothing. So the overlay follows the playhead instead:
        // no movement means the user is actually watching a frozen frame.
        _stallTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _stallTimer.Tick += (_, _) => CheckStall();

        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SeekSlider_PointerPressed), true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SeekSlider_PointerReleased), true);
        SeekSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(SeekSlider_PointerCaptureLost), true);
        Root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Root_PointerMoved), true);
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Root_PointerPressed), true);
        Root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Root_PointerWheelChanged), true);
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Root_KeyDown), true);
        Root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(Root_PreviewKeyDown), true);
        Root.SizeChanged += Root_SizeChanged;

        Loaded += (_, _) =>
        {
            _engine.FocusSurface();
            SyncLoadingBreath();

            // Discovery is fast when the details page prefetched it, which can
            // beat the page's own load; the picker waits for its anchor.
            if (_sourcesFlyoutPending)
            {
                _sourcesFlyoutPending = false;
                OpenSources();
            }
        };

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        VolumeSlider.Value = 80;
    }

    /// <summary>
    /// Breathes the title artwork on the startup overlay. The storyboard targets
    /// are bound here rather than by TargetName because a storyboard that lives
    /// in Page.Resources cannot see the page's namescope.
    /// </summary>
    private void ConfigureLoadingBreath()
    {
        _loadingBreath = Resources["LoadingBreathStoryboard"] as Storyboard;
        if (_loadingBreath is null)
        {
            return;
        }

        foreach (var animation in _loadingBreath.Children)
        {
            Storyboard.SetTarget(animation, LoadingBrand);
        }

        LoadingOverlay.RegisterPropertyChangedCallback(
            VisibilityProperty,
            (_, _) => SyncLoadingBreath());
    }

    /// <summary>
    /// Runs the pulse only while the overlay is actually on screen, and leaves
    /// it off under reduce motion or a High Contrast theme where fading the
    /// title would hurt legibility.
    /// </summary>
    private void SyncLoadingBreath()
    {
        if (_loadingBreath is null)
        {
            return;
        }

        var shouldRun = LoadingOverlay.IsLoaded &&
                        LoadingOverlay.Visibility == Visibility.Visible &&
                        _uiSettings.AnimationsEnabled &&
                        !_accessibility.HighContrast;

        if (shouldRun)
        {
            if (_loadingBreath.GetCurrentState() != ClockState.Active)
            {
                _loadingBreath.Begin();
            }

            return;
        }

        _loadingBreath.Stop();
        LoadingBrand.Opacity = 1;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not PlaybackRequest request)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                HideLoadingOverlay();
                ShowPlaybackError("This title is missing playback information.", canSwitch: false);
            });
            return;
        }

        ViewModel.Request = request;
        PlayerLog.Write(
            $"open {request.Type} id={request.VideoId} S{request.Season:00}E{request.Episode:00} " +
            $"title={request.Title}");
        _selectedSubtitle = null;
        _resumeFromHistoryMs = 0;
        _positionMs = 0;
        _lengthMs = 0;
        _ended = false;
        _subtitleDelayMs = 0;
        _selectedEmbeddedTrackId = -1;
        _selectedEmbeddedName = null;
        _selectedEmbeddedCode = null;
        ViewModel.SetEmbeddedSubtitles([]);
        UpdateSubtitleTimingUi();
        ResumePanel.Visibility = Visibility.Collapsed;
        HideUpNext();
        _upNextDismissed = false;
        ViewModel.NextEpisode = null;
        ViewModel.ClearSeries();
        _ = LoadSourcesAsync(request);
        _ = LoadSubtitlesAsync(request);
        _ = LoadNextEpisodeAsync(request);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        DisposePlayback();
        if (App.Window is MainWindow shell)
        {
            shell.SetPlayerFullscreen(false);
        }
    }

    // -------------------------------------------------------------- engine events

    private void HookEngineEvents()
    {
        _engine.Ready += Engine_Ready;
        _engine.Playing += (_, _) => Dispatch(HandlePlaying);
        _engine.Paused += (_, _) => Dispatch(HandlePaused);
        _engine.Stopped += (_, _) => Dispatch(HandleStopped);
        _engine.EndReached += (_, _) => Dispatch(HandleEndReached);
        _engine.Failed += (_, e) => Dispatch(() => HandleEngineFailed(e));
        _engine.PositionChanged += (_, e) =>
            Dispatch(() => HandleTimeChanged(e.PositionMs));
        _engine.DurationChanged += (_, e) =>
            Dispatch(() => HandleLengthChanged(e.DurationMs));
        _engine.BufferingChanged += (_, e) =>
            Dispatch(() => HandleBuffering(e.CachePercent));
        _engine.TracksChanged += (_, e) => HandleEsChanged(e.Kind);

        // A warm engine can finish before the page subscribes; don't miss that.
        if (_engine.IsReady)
        {
            Engine_Ready(_engine, EventArgs.Empty);
        }
    }

    private void Engine_Ready(object? sender, EventArgs e)
    {
        _engine.FocusSurface();
        TryStartPlayback();
    }

    private void HandleEngineFailed(PlaybackErrorEventArgs e)
    {
        PlayerLog.Write($"engine error, state {_engine.State}, url {_currentSource?.Url}");

        // A remembered source that fails before playing is replaced silently;
        // a backend that cannot start at all has nothing to fall back to.
        if (!e.IsFatal && _cachedAttempt is not null)
        {
            _ = HandleCachedSourceFailureAsync();
            return;
        }

        ShowPlaybackError(e.Message, e.IsFatal ? false : ViewModel.HasSources);
    }

    private async Task LoadSubtitlesAsync(PlaybackRequest request)
    {
        _subtitleCts?.Cancel();
        _subtitleCts?.Dispose();
        _subtitleCts = new CancellationTokenSource();

        try
        {
            var token = _subtitleCts.Token;
            await App.Settings.EnsureLoadedAsync(token);

            // A language picked for this title wins over the app preference, so
            // watching one film with French subtitles does not change the
            // default for every other title.
            var titleKey = SubtitleOffsetKey(request);
            var remembered = await App.SubtitleChoices.GetLanguageAsync(titleKey, token);

            await ViewModel.LoadSubtitlesAsync(
                request,
                token,
                App.Settings.PreferredSubtitleLanguage);

            var automaticLanguage = string.IsNullOrWhiteSpace(remembered)
                ? App.Settings.PreferredSubtitleLanguage
                : remembered;

            // Subtitles for the chosen language turn on without opening the
            // picker, unless something was already selected for this title.
            if (!_disposed &&
                !token.IsCancellationRequested &&
                !HasActiveSubtitle &&
                ViewModel.FindSubtitleForLanguage(automaticLanguage) is { } preferred)
            {
                // Playback may not have started yet; the track is attached when it does.
                if (!await ApplySelectedSubtitleAsync(preferred, applyNow: false, remember: false))
                {
                    // Fall back to Off rather than showing a track that never loaded.
                    RestoreSubtitleSelection(null);
                }
            }

            if (SubtitlesFlyout.IsOpen)
            {
                RefreshSubtitleFlyout();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RefreshSubtitleFlyout()
    {
        if (SubtitleLanguageList.ItemsSource is null)
        {
            SubtitleLanguageList.ItemsSource = ViewModel.SubtitleMenu;
        }

        if (_versionsLanguage is not null)
        {
            SubtitleVersionList.ItemsSource ??= _versionsLanguage.Versions;
        }

        var status = ViewModel.SubtitleStatus;
        SubtitleStatusText.Text = status;
        SubtitleStatusPanel.Visibility = string.IsNullOrWhiteSpace(status)
            ? Visibility.Collapsed
            : Visibility.Visible;
        SubtitleLoadingRing.IsActive = ViewModel.IsLoadingSubtitles;
        SubtitleLoadingRing.Visibility = ViewModel.IsLoadingSubtitles
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateSubtitleCurrent();
        UpdateSubtitleTimingUi();
    }

    private void UpdateSubtitleCurrent()
    {
        var current = _selectedSubtitle is { } version
            ? $"{version.LanguageName} · {version.Label}"
            : _selectedEmbeddedName is { Length: > 0 } embedded
                ? $"{embedded} · Embedded"
                : null;

        if (current is null)
        {
            SubtitleCurrentText.Text = "";
            SubtitleCurrentText.Visibility = Visibility.Collapsed;
        }
        else
        {
            SubtitleCurrentText.Text = $"Current: {current}";
            SubtitleCurrentText.Visibility = Visibility.Visible;
        }

        if (SubtitleVersionView.Visibility == Visibility.Visible && _versionsLanguage is not null)
        {
            SubtitleVersionHint.Text = _selectedSubtitle is not null &&
                                       _selectedSubtitle.LanguageCode == _versionsLanguage.LanguageCode
                ? $"Current: {_selectedSubtitle.Label}"
                : "Choose the version that matches your release.";
        }
    }

    // ------------------------------------------------------------- source loading

    private async Task LoadSourcesAsync(PlaybackRequest request, bool autoStart = false)
    {
        _sourceCts?.Cancel();
        _sourceCts?.Dispose();
        _sourceCts = new CancellationTokenSource();

        if (ViewModel.Request != request)
        {
            ViewModel.Request = request;
        }

        await App.Settings.EnsureLoadedAsync();
        await RestoreProgressAsync(request);
        await RestoreSubtitleDelayAsync(request);

        // Fast path: start the last source that actually played, while the
        // normal source search runs in parallel. A dead URL therefore costs
        // at most the watchdog window, never the full discovery wait.
        var savedSource = await FindRememberedSourceAsync(request);
        if (savedSource is not null && (App.Settings.Autoplay || autoStart))
        {
            _sourceDiscovery = ViewModel.LoadAsync(request, _sourceCts.Token);
            _ = CompleteSourceDiscoveryAsync(_sourceDiscovery);

            _cachedAttempt = savedSource;
            _pendingSource = savedSource;
            ViewModel.StatusText = "Starting your last source…";
            TryStartPlayback();
            return;
        }

        var loaded = await ViewModel.LoadAsync(request, _sourceCts.Token);
        if (_disposed)
        {
            return;
        }

        if (!loaded)
        {
            HideLoadingOverlay();
            SourcesButton.IsEnabled = false;
            ShowPlaybackError(ViewModel.ErrorMessage, canSwitch: false);
            return;
        }

        SourcesButton.IsEnabled = true;
        ShowChrome();

        var autoplay = SelectAutoplaySource(force: autoStart);
        if (autoplay is not null)
        {
            _pendingSource = autoplay;
            TryStartPlayback();
            return;
        }

        // Nothing to auto-play yet: keep the artwork up so the picker sits on
        // the poster instead of the bare video surface, and let the user choose.
        ShowPickerBackdrop();
        OpenSources();
    }

    /// <summary>
    /// The last source that reached "playing" for this exact movie or episode,
    /// when it is still fresh and still belongs to a configured addon.
    /// </summary>
    private async Task<PlaybackSource?> FindRememberedSourceAsync(PlaybackRequest request)
    {
        try
        {
            await App.Addons.EnsureLoadedAsync();

            var entry = App.WatchProgress.Find(request.VideoId);
            if (entry is not { IsSourceFresh: true, SourceUrl: { Length: > 0 } url })
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(entry.SourceAddonUrl) &&
                App.Addons.GetEnabledEntry(entry.SourceAddonUrl) is null)
            {
                _ = App.WatchProgress.ForgetSourceAsync(request.VideoId);
                return null;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            return new PlaybackSource
            {
                Url = url,
                AddonName = string.IsNullOrWhiteSpace(entry.SourceAddon) ? "Saved source" : entry.SourceAddon,
                AddonUrl = entry.SourceAddonUrl,
                QualityLabel = string.IsNullOrWhiteSpace(entry.SourceQuality) ? "Auto" : entry.SourceQuality,
                ProviderLine = "Last used",
                Headers = entry.SourceHeaders,
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task CompleteSourceDiscoveryAsync(Task<bool> discovery)
    {
        try
        {
            var loaded = await discovery;
            if (!_disposed)
            {
                SourcesButton.IsEnabled = loaded;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The cached source is playing; discovery failures are invisible.
        }
    }

    /// <summary>
    /// A remembered source didn't play. Drop it and fall back to the source
    /// list that was fetched in the background, without showing an error.
    /// </summary>
    private async Task HandleCachedSourceFailureAsync()
    {
        if (_cachedAttempt is null || _disposed)
        {
            return;
        }

        var failed = _cachedAttempt;
        _cachedAttempt = null;
        _cachedWatchdog?.Stop();

        // Stop the dead attempt now so a late "playing" event can't take over
        // the player while the fallback is being prepared.
        _switching = true;
        _engine.Stop();
        _currentSource = null;
        _playing = false;

        var request = ViewModel.Request;
        if (request is not null)
        {
            await App.WatchProgress.ForgetSourceAsync(request.VideoId);
        }

        if (_disposed)
        {
            return;
        }

        _errorVisible = false;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ShowLoadingOverlay("That saved source didn't work. Finding another…");

        var discovery = _sourceDiscovery;
        if (discovery is null)
        {
            if (request is null)
            {
                ShowPlaybackError("Couldn't find a source.", canSwitch: false);
                return;
            }

            discovery = ViewModel.LoadAsync(request, _sourceCts?.Token ?? CancellationToken.None);
        }

        bool loaded;
        try
        {
            loaded = await discovery;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_disposed)
        {
            return;
        }

        _sourceDiscovery = null;
        SourcesButton.IsEnabled = loaded;

        if (!loaded)
        {
            ShowPlaybackError(ViewModel.ErrorMessage, canSwitch: false);
            return;
        }

        ShowChrome();

        var next = SelectAutoplaySource(failed.Url);

        // Sources may still be arriving; give the remaining addons a chance
        // before showing the picker with a single failed entry.
        if (next is null && ViewModel.IsDiscovering)
        {
            try
            {
                await ViewModel.WaitForSourcesAsync(_sourceCts?.Token ?? CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            next = SelectAutoplaySource(failed.Url);
        }

        if (next is not null)
        {
            _pendingSource = next;
            TryStartPlayback();
            return;
        }

        ShowPickerBackdrop();
        OpenSources();
    }

    /// <summary>
    /// Picks up the locally saved position for this movie or episode. The
    /// position is applied to the first source played; switching sources keeps
    /// the live playback position instead.
    /// </summary>
    private async Task RestoreProgressAsync(PlaybackRequest request)
    {
        try
        {
            await App.WatchProgress.EnsureLoadedAsync();
            var saved = App.WatchProgress.Find(request.VideoId);

            if (saved is { Completed: false, PositionMs: > 0 })
            {
                _resumeFromHistoryMs = saved.PositionMs;
                ResumeText.Text = $"Resuming from {FormatTime(saved.PositionMs)}";
                ResumePanel.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            // Watch history is optional; playback starts from the beginning.
        }
    }

    private void HideResumeHint()
    {
        _resumeHintTimer?.Stop();
        ResumePanel.Visibility = Visibility.Collapsed;
    }

    private void StartOver_Click(object sender, RoutedEventArgs e)
    {
        _resumeFromHistoryMs = 0;

        if (_engine.IsSeekable && _currentSource is not null)
        {
            _engine.PositionMs = 0;
            _positionMs = 0;
        }

        HideResumeHint();
        ShowChrome();
        StartChromeTimer();
    }

    private PlaybackSource? SelectAutoplaySource(string? avoidUrl = null, bool force = false)
    {
        if (!force && !App.Settings.Autoplay)
        {
            return null;
        }

        IEnumerable<PlaybackSource> candidates = ViewModel.Sources;
        if (!string.IsNullOrWhiteSpace(avoidUrl))
        {
            candidates = candidates.Where(
                source => !source.Url.Equals(avoidUrl, StringComparison.OrdinalIgnoreCase));
        }

        var preferred = App.Settings.PreferredQuality;
        if (string.IsNullOrWhiteSpace(preferred))
        {
            return candidates.FirstOrDefault();
        }

        return candidates.FirstOrDefault(
                   source => source.QualityLabel.Equals(preferred, StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault();
    }

    // ------------------------------------------------------------ next episode

    /// <summary>True when the request is one episode of a series.</summary>
    private static bool IsEpisodic(PlaybackRequest request) =>
        request.Episode > 0 &&
        (request.Type.Equals("series", StringComparison.OrdinalIgnoreCase) ||
         request.Type.Equals("tv", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves the episode after the current one. Requests coming from the
    /// details page already carry the full episode list; a Continue watching
    /// entry does not, so the series metadata is fetched in the background.
    /// </summary>
    private async Task LoadNextEpisodeAsync(PlaybackRequest request)
    {
        _nextEpisodeCts?.Cancel();
        _nextEpisodeCts?.Dispose();
        _nextEpisodeCts = new CancellationTokenSource();
        var cancellationToken = _nextEpisodeCts.Token;

        _nextEpisode = null;
        ViewModel.NextEpisode = null;
        HideUpNext();

        if (!IsEpisodic(request))
        {
            ViewModel.ClearSeries();
            return;
        }

        try
        {
            var meta = request.Meta;
            if (meta.Videos is not { Count: > 1 })
            {
                meta = await FetchSeriesMetaAsync(meta, cancellationToken) ?? meta;
            }

            if (cancellationToken.IsCancellationRequested || _disposed)
            {
                return;
            }

            _seriesMeta = meta;
            await ViewModel.SetSeriesAsync(meta, request, cancellationToken);
            if (cancellationToken.IsCancellationRequested || _disposed)
            {
                return;
            }

            _nextEpisode = FindNextEpisode(meta, request.Season, request.Episode);
            ViewModel.NextEpisode = _nextEpisode;
            UpdateUpNextVisibility();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            _nextEpisode = null;
            ViewModel.NextEpisode = null;
        }
    }

    private async Task<MetaItem?> FetchSeriesMetaAsync(
        MetaItem meta,
        CancellationToken cancellationToken)
    {
        var seriesId = meta.Id;
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return null;
        }

        try
        {
            await App.Addons.EnsureLoadedAsync(cancellationToken);
            await App.Addons.LoadManifestsAsync(cancellationToken);

            // Same priority order as the details page. The source addon is
            // part of the list; custom ids only match the addon that declared
            // their prefix.
            var clients = App.Addons.MetaCandidates("series", seriesId, meta.SourceAddonUrl)
                .Select(entry => entry.Client)
                .ToList();

            if (clients.Count == 0 && App.Addons.GetEnabledEntry(meta.SourceAddonUrl)?.Client is { } preferred)
            {
                clients.Add(preferred);
            }

            foreach (var client in clients)
            {
                try
                {
                    var full = await client.GetMetaAsync("series", seriesId, cancellationToken);
                    if (full.Videos is { Count: > 0 })
                    {
                        return full;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Try the next metadata addon.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Without metadata there is simply no Up next card.
        }

        return null;
    }

    private static MetaVideo? FindNextEpisode(MetaItem meta, int season, int episode)
    {
        var ordered = meta.Videos?
            .Where(video => video.EpisodeNumber > 0)
            .OrderBy(video => video.Season == 0 ? int.MaxValue : video.Season)
            .ThenBy(video => video.EpisodeNumber)
            .ToList();

        if (ordered is not { Count: > 0 })
        {
            return null;
        }

        var index = ordered.FindIndex(video =>
            video.Season == season && video.EpisodeNumber == episode);

        return index >= 0 && index + 1 < ordered.Count ? ordered[index + 1] : null;
    }

    /// <summary>
    /// The card appears a little before the end (5%, clamped to 15–90 seconds)
    /// so the credits can be skipped, and again when playback finishes.
    /// </summary>
    private void UpdateUpNextVisibility()
    {
        if (_disposed)
        {
            return;
        }

        if (_nextEpisode is null || _upNextDismissed)
        {
            HideUpNext();
            return;
        }

        if (!_ended)
        {
            if (_lengthMs <= 0)
            {
                return;
            }

            var lead = Math.Clamp((long)(_lengthMs * 0.05), 15_000, 90_000);
            if (_lengthMs - _positionMs > lead)
            {
                HideUpNext();
                return;
            }
        }

        ShowUpNext();
    }

    private void ShowUpNext()
    {
        if (_nextEpisode is null || _upNextDismissed)
        {
            return;
        }

        if (UpNextPanel.Visibility != Visibility.Visible)
        {
            UpNextPanel.Visibility = Visibility.Visible;
        }
    }

    private void HideUpNext()
    {
        if (UpNextPanel.Visibility != Visibility.Collapsed)
        {
            UpNextPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void PlayNext_Click(object sender, RoutedEventArgs e) => _ = PlayNextEpisodeAsync();

    private void DismissUpNext_Click(object sender, RoutedEventArgs e)
    {
        // Also cancels the automatic switch for this episode.
        _upNextDismissed = true;
        HideUpNext();
    }

    /// <summary>
    /// Switches the same player to the next episode, keeping the current
    /// window, chrome and subtitles logic. The next episode's remembered
    /// source is used when available; otherwise the best source is chosen.
    /// </summary>
    private Task PlayNextEpisodeAsync() =>
        _nextEpisode is { } next ? PlayEpisodeAsync(next) : Task.CompletedTask;

    /// <summary>
    /// Switches the same player to any episode of the series: saves the
    /// current position, resets per-episode state, and starts the new episode
    /// with its remembered source or the best available one.
    /// </summary>
    private async Task PlayEpisodeAsync(MetaVideo episode)
    {
        if (_advancing || _disposed || _seriesMeta is not { } series)
        {
            return;
        }

        if (ViewModel.Request is { } current &&
            current.Season == episode.Season &&
            current.Episode == episode.EpisodeNumber &&
            !_errorVisible)
        {
            return;
        }

        _advancing = true;
        try
        {
            // Save the finished (or skipped) episode before the request changes.
            PersistProgress(_positionMs, _lengthMs);

            var request = PlaybackRequest.ForEpisode(series, episode);

            _nextEpisode = null;
            _upNextDismissed = false;
            ViewModel.NextEpisode = null;
            HideUpNext();

            PrepareNextEpisode();

            ViewModel.Request = request;
            ViewModel.MarkCurrentEpisode(episode.Season, episode.EpisodeNumber);
            _ = LoadSubtitlesAsync(request);
            _ = LoadNextEpisodeAsync(request);
            await LoadSourcesAsync(request, autoStart: true);
        }
        finally
        {
            _advancing = false;
        }
    }

    /// <summary>Resets per-episode state before the next episode is loaded.</summary>
    private void PrepareNextEpisode()
    {
        _cachedAttempt = null;
        _cachedWatchdog?.Stop();
        _sourceDiscovery = null;
        _nextEpisodeCts?.Cancel();
        _switching = true;
        _engine.Stop();
        _currentSource = null;

        var staleCache = _streamCache;
        _streamCache = null;
        if (staleCache is not null)
        {
            _ = staleCache.DisposeAsync().AsTask();
        }

        _playing = false;
        _ended = false;
        _errorVisible = false;
        _positionMs = 0;
        _lengthMs = 0;
        _resumeMs = 0;
        _resumeFromHistoryMs = 0;
        _upNextDismissed = false;

        _subtitleApplied = false;
        _selectedSubtitle = null;
        _selectedSubtitlePath = null;
        _versionsLanguage = null;
        _selectedEmbeddedTrackId = -1;
        _selectedEmbeddedName = null;
        _selectedEmbeddedCode = null;
        ViewModel.SetEmbeddedSubtitles([]);
        UpdateSubtitleTimingUi();
        UpdateSubtitleIndicator();
        ViewModel.SelectSubtitle(null);
        ViewModel.SubtitleStatus = "";

        if (SubtitlesFlyout.IsOpen)
        {
            SubtitlesFlyout.Hide();
        }

        if (EpisodesFlyout.IsOpen)
        {
            EpisodesFlyout.Hide();
        }

        if (_sourcesFlyoutOpen)
        {
            CloseSources();
        }

        _resumeHintTimer?.Stop();
        ResumePanel.Visibility = Visibility.Collapsed;
        EndedText.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        HideBuffer();
        HideUpNext();
        ShowLoadingOverlay();
        UpdatePlayPauseUi();
        ShowChrome();
    }

    private void TryStartPlayback()
    {
        if (_disposed || !_engine.IsReady || _pendingSource is null)
        {
            return;
        }

        var source = _pendingSource;
        _pendingSource = null;
        PlaySource(source);
    }

    private void PlaySource(PlaybackSource source) => _ = PlaySourceAsync(source);

    /// <summary>
    /// Resolves where the source should actually play from, then hands it to
    /// the engine. On-demand files go through a local read-ahead cache so a seek
    /// is a disk read instead of another trip to the provider; anything the
    /// cache can't help with (live TV, HLS, servers without range support)
    /// plays straight from the addon URL.
    /// </summary>
    private async Task PlaySourceAsync(PlaybackSource source)
    {
        // Keep the title artwork up for the whole start-up window: source
        // discovery, cache setup, and the engine opening the stream.
        // HandlePlaying takes it down once there are actual frames.
        ShowLoadingOverlay("Starting stream…");

        if (!_engine.IsReady)
        {
            _pendingSource = source;
            return;
        }

        var attempt = ++_playAttempt;
        var url = source.Url;
        StreamCacheSession? cache = null;

        if (ShouldCacheSource(source))
        {
            try
            {
                cache = await StreamCacheSession.TryCreateAsync(
                    new Uri(source.Url), source.Headers, _streamCacheCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                // A broken cache must never stop a source from playing.
                PlayerLog.Write($"cache setup failed, playing direct: {exception.Message}");
            }

            if (_disposed || attempt != _playAttempt)
            {
                if (cache is not null)
                {
                    await cache.DisposeAsync();
                }

                return;
            }

            if (cache is not null)
            {
                url = cache.Url;
            }
        }

        PlaySourceCore(source, url, cache);
    }

    /// <summary>Only plain on-demand HTTP files benefit from the local cache.</summary>
    private bool ShouldCacheSource(PlaybackSource source)
    {
        if (ViewModel.Request is null || IsLive(ViewModel.Request) || source.IsHls)
        {
            return false;
        }

        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        return !path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) &&
               !path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) &&
               !path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
    }

    private void PlaySourceCore(PlaybackSource source, string url, StreamCacheSession? cache)
    {
        if (!_engine.IsReady)
        {
            return;
        }

        try
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            _errorVisible = false;

            var resume = !_ended &&
                         _engine.State is PlaybackState.Playing
                             or PlaybackState.Paused
                             or PlaybackState.Buffering
                ? Math.Max(0, _engine.PositionMs)
                : 0;

            if (resume > 0 && _lengthMs > 0)
            {
                PersistProgress(resume, _lengthMs);
            }

            // The first source of this title continues from the local history.
            if (resume == 0 && _resumeFromHistoryMs > 0)
            {
                resume = _resumeFromHistoryMs;
                _lastProgressSaveUtc = DateTime.UtcNow;
            }

            _ended = false;
            _resumeMs = resume;
            _positionMs = resume;
            _lengthMs = 0;
            _switching = true;
            _engine.Stop();

            var previousCache = _streamCache;
            _streamCache = cache;
            if (previousCache is not null)
            {
                _ = previousCache.DisposeAsync().AsTask();
            }

            // The discarded source's tracks must not leak into the new menu.
            _audioTracks = [];
            _selectedAudioTrackId = -1;
            _audioListDirty = true;
            UpdateAudioIndicator();

            // Embedded subtitle ids belong to the previous stream. The chosen
            // language is kept so the same track can be re-selected when the
            // new stream exposes its own tracks.
            _selectedEmbeddedTrackId = -1;
            _selectedEmbeddedName = null;
            ViewModel.SetEmbeddedSubtitles([]);
            UpdateSubtitleCurrent();
            UpdateSubtitleTimingUi();

            var media = new PlaybackMedia(
                url,
                StartPositionMs: resume,
                IsLive: IsLive(ViewModel.Request),
                IsHls: source.IsHls,
                // The proxy already attaches the provider headers upstream,
                // and sending them to 127.0.0.1 would only confuse the engine.
                Headers: cache is null ? source.Headers : null);

            PlayerLog.Write(
                $"play quality={source.QualityLabel} format={source.FormatLabel} " +
                $"hls={source.IsHls} live={IsLive(ViewModel.Request)} " +
                $"resume={resume}ms local={cache is not null} " +
                $"headers=[{string.Join(',', source.Headers?.Keys ?? [])}] url={url}");
            _seekWatch.Restart();

            _currentSource = source;
            _subtitleApplied = false;

            if (ReferenceEquals(source, _cachedAttempt))
            {
                _cachedWatchdog?.Stop();
                _cachedWatchdog?.Start();
            }

            foreach (var candidate in ViewModel.Sources)
            {
                candidate.IsCurrent = ReferenceEquals(candidate, source) ||
                                      candidate.Url.Equals(source.Url, StringComparison.OrdinalIgnoreCase);
            }

            ViewModel.SelectedSource = source;
            CloseSources();
            ShowBuffer("Starting stream…");
            if (!_engine.TryPlay(media))
            {
                throw new InvalidOperationException("The video engine couldn't start this source.");
            }

            ShowChrome();
            StartChromeTimer();
        }
        catch (Exception exception)
        {
            _switching = false;

            if (_cachedAttempt is not null)
            {
                _ = HandleCachedSourceFailureAsync();
                return;
            }

            ShowPlaybackError($"Couldn't start this source. {exception.Message}", ViewModel.HasSources);
        }
    }

    /// <summary>Live TV channel: "tv"/"channel" type with no episode attached.</summary>
    private static bool IsLive(PlaybackRequest? request) =>
        request is not null &&
        request.Episode == 0 &&
        (request.Type.Equals("tv", StringComparison.OrdinalIgnoreCase) ||
         request.Type.Equals("channel", StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------ audio tracks

    private void HandleEsChanged(PlaybackTrackKind kind)
    {
        if (_switching)
        {
            return;
        }

        if (kind is PlaybackTrackKind.Audio)
        {
            // Elementary-stream events arrive off the UI thread.
            _audioListDirty = true;
            Dispatch(() =>
            {
                if (_audioListDirty)
                {
                    RefreshAudioTracks();
                }

                if (AudioMenu.IsOpen)
                {
                    RebuildAudioMenu();
                }
            });
            return;
        }

        if (kind is PlaybackTrackKind.Subtitle)
        {
            Dispatch(() =>
            {
                RefreshEmbeddedSubtitles();
                if (SubtitlesFlyout.IsOpen)
                {
                    RefreshSubtitleFlyout();
                }
            });
        }
    }

    /// <summary>
    /// Streams are parsed as the media starts, so the track list is complete
    /// only once playback settles. Read it lazily and keep it fresh with the
    /// ES* events afterwards.
    /// </summary>
    private void RefreshAudioTracks()
    {
        try
        {
            _audioTracks = _engine.AudioTracks;
            _selectedAudioTrackId = _engine.CurrentAudioTrackId;
        }
        catch
        {
            _audioTracks = [];
            _selectedAudioTrackId = -1;
        }

        _audioListDirty = false;
        UpdateAudioIndicator();
    }

    private void AudioMenu_Opened(object? sender, object e)
    {
        if (_audioListDirty || _engine.State != PlaybackState.Playing || AudioMenu.Items.Count == 0)
        {
            RefreshAudioTracks();
        }

        RebuildAudioMenu();
    }

    private void RebuildAudioMenu()
    {
        AudioMenu.Items.Clear();

        if (!_engine.IsReady || _audioTracks.Count == 0)
        {
            AudioMenu.Items.Add(new MenuFlyoutItem { Text = "No audio tracks", IsEnabled = false });
            return;
        }

        foreach (var track in _audioTracks)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = track.DisplayName,
                IsChecked = track.Id == _selectedAudioTrackId,
                Tag = track.Id,
            };
            item.Click += AudioTrack_Click;
            AudioMenu.Items.Add(item);
        }
    }

    private void AudioTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: long id } || !_engine.IsReady)
        {
            return;
        }

        if (!_engine.SelectAudioTrack(id))
        {
            return;
        }

        _selectedAudioTrackId = id;
        foreach (var item in AudioMenu.Items.OfType<ToggleMenuFlyoutItem>())
        {
            item.IsChecked = item.Tag is long other && other == id;
        }
    }

    /// <summary>
    /// Subtitles have no chrome of their own, so the CC button carries the
    /// state: the icon turns brand-coloured while a track is showing.
    /// </summary>
    private void UpdateSubtitleIndicator() =>
        SubtitlesIcon.Style = HasActiveSubtitle
            ? Resources["PlayerIconActiveStyle"] as Style
            : null;

    /// <summary>
    /// A second audio track is a choice worth advertising; the glyph itself
    /// carries the state instead of a dot in the button's corner.
    /// </summary>
    private void UpdateAudioIndicator() =>
        AudioIcon.Style = _audioTracks.Count > 1
            ? Resources["PlayerIconActiveStyle"] as Style
            : null;

    private void Dispatch(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed)
                {
                    action();
                }
            });
        }
    }

    private void HandlePlaying()
    {
        PlayerLog.Write(
            $"playing at {_engine.PositionMs}ms {_seekWatch.ElapsedMilliseconds}ms after the last seek/start");
        _switching = false;
        _playing = true;
        _ended = false;
        _errorVisible = false;
        ResetStallClock(_engine.PositionMs);
        _stallTimer.Start();

        HideLoadingOverlay();
        ErrorPanel.Visibility = Visibility.Collapsed;
        EndedText.Visibility = Visibility.Collapsed;
        HideBuffer();
        UpdatePlayPauseUi();
        SeekSlider.IsEnabled = _engine.IsSeekable;
        ShowChrome();
        StartChromeTimer();

        if (ResumePanel.Visibility == Visibility.Visible)
        {
            _resumeHintTimer?.Stop();
            _resumeHintTimer?.Start();
        }

        if (_resumeMs > 0 && _engine.IsSeekable)
        {
            _engine.PositionMs = _resumeMs;
        }

        _resumeMs = 0;
        _resumeFromHistoryMs = 0;
        _cachedAttempt = null;
        _cachedWatchdog?.Stop();

        // Attach the working source to the history entry as early as possible.
        PersistProgress(_positionMs, _lengthMs);

        if (!_subtitleApplied &&
            _selectedSubtitle is not null &&
            !string.IsNullOrWhiteSpace(_selectedSubtitlePath))
        {
            ApplyLocalSubtitle(_selectedSubtitlePath);
        }

        // The engine resets subtitle timing for every new media.
        ApplySubtitleDelay();
        UpdateSubtitleTimingUi();

        // Tracks that parsed while the new source was starting can be picked
        // up here; later ones arrive through the ES events.
        RefreshEmbeddedSubtitles();

        // A new picture means a new vout, and a new vout starts with no lift.
        _appliedSubtitleLift = -1;
        RefreshSubtitleLift();
    }

    private void HandlePaused()
    {
        _playing = false;
        _stallTimer.Stop();
        HideBuffer();
        PersistProgress(_positionMs, _lengthMs);
        UpdatePlayPauseUi();
        ShowChrome();
        _chromeTimer.Stop();
    }

    private void HandleStopped()
    {
        if (_disposed)
        {
            return;
        }

        if (_cachedAttempt is not null)
        {
            _ = HandleCachedSourceFailureAsync();
            return;
        }

        if (_switching)
        {
            return;
        }

        PersistProgress(_positionMs, _lengthMs);
        _playing = false;
        _stallTimer.Stop();
        HideBuffer();
        UpdatePlayPauseUi();
        ShowChrome();
    }

    private void HandleEndReached()
    {
        // A source that never reached "playing" is a failed attempt, not a
        // finished title. Marking it completed would hide it from Continue
        // watching and lose the resume position.
        if (_cachedAttempt is not null)
        {
            _ = HandleCachedSourceFailureAsync();
            return;
        }

        if (_switching)
        {
            return;
        }

        _playing = false;
        _ended = true;
        _stallTimer.Stop();
        _positionMs = _lengthMs > 0 ? _lengthMs : _positionMs;
        PersistProgress(_positionMs, _lengthMs, completed: true);

        if (_nextEpisode is not null && !_upNextDismissed && App.Settings.AutoAdvance)
        {
            _ = PlayNextEpisodeAsync();
            return;
        }

        UpdateUpNextVisibility();
        HideResumeHint();
        HideBuffer();
        EndedText.Visibility = Visibility.Visible;
        UpdatePlayPauseUi();
        ShowChrome();
        _chromeTimer.Stop();
    }

    private void HandleTimeChanged(long time)
    {
        _positionMs = Math.Max(0, time);
        UpdateUpNextVisibility();

        if (_draggingSeek)
        {
            return;
        }

        TrackProgress();

        _suppressSeek = true;
        SeekSlider.Value = Math.Clamp(time, 0, (long)SeekSlider.Maximum);
        _suppressSeek = false;
        UpdateTimeText(time, _engine.DurationMs);
    }

    private void HandleLengthChanged(long length)
    {
        _lengthMs = Math.Max(0, length);
        UpdateUpNextVisibility();
        SeekSlider.Maximum = Math.Max(1, length);
        SeekSlider.IsEnabled = length > 0 && _engine.IsSeekable;
        UpdateTimeText(_engine.PositionMs, length);
    }

    /// <summary>Writes the position at most every ten seconds while playing.</summary>
    private void TrackProgress()
    {
        if (_positionMs <= 0 || _lengthMs <= 0)
        {
            return;
        }

        if ((DateTime.UtcNow - _lastProgressSaveUtc).TotalSeconds < 10)
        {
            return;
        }

        PersistProgress(_positionMs, _lengthMs);
    }

    /// <summary>
    /// Saves to the local watch history. Nothing leaves the machine, and a
    /// failure here never interrupts playback.
    /// </summary>
    private async void PersistProgress(long positionMs, long durationMs, bool completed = false)
    {
        if (ViewModel.Request is not { } request)
        {
            return;
        }

        if (!completed && (durationMs <= 0 || positionMs < WatchProgressEntry.MinimumPositionMs))
        {
            return;
        }

        _lastProgressSaveUtc = DateTime.UtcNow;

        try
        {
            await App.WatchProgress.SaveAsync(
                request,
                positionMs,
                durationMs,
                completed,
                ToSourceInfo(_currentSource));
        }
        catch
        {
            // History is best effort.
        }
    }

    private static PlaybackSourceInfo? ToSourceInfo(PlaybackSource? source) =>
        source is null
            ? null
            : new PlaybackSourceInfo(
                source.Url,
                source.AddonName,
                source.AddonUrl,
                source.QualityLabel,
                source.Headers);

    private void HandleBuffering(float cache)
    {
        if (_ended || _errorVisible)
        {
            return;
        }

        if (cache is > 0 and < 100 && _playing)
        {
            if (!_bufferingLogged)
            {
                _bufferingLogged = true;
                PlayerLog.Write($"buffering reported at {cache:0}%");
            }

            _bufferText = $"Buffering… {cache:0}%";
            if (BufferPanel.Visibility == Visibility.Visible)
            {
                BufferText.Text = _bufferText;
            }
        }
        else if (cache >= 100)
        {
            if (_bufferingLogged)
            {
                _bufferingLogged = false;
                PlayerLog.Write($"buffering reached 100% after {_seekWatch.ElapsedMilliseconds}ms");
            }
        }
    }

    /// <summary>
    /// Shows the buffering overlay only while the playhead is genuinely stuck,
    /// and takes it down the moment frames move again. This is the only honest
    /// stall signal the player has: engines report "Playing" over a frozen
    /// picture, and partial buffering over playback that is fine.
    /// </summary>
    private void CheckStall()
    {
        if (_disposed || !_playing || _ended || _errorVisible || _draggingSeek || !_engine.IsReady)
        {
            return;
        }

        var position = Math.Max(0, _engine.PositionMs);
        if (position != _advancePositionMs)
        {
            _advancePositionMs = position;
            _advanceUtc = DateTime.UtcNow;
        }

        // Draining the last frames is not a stall, whatever the clock says.
        var length = _engine.DurationMs;
        var draining = length > 0 && position >= length - EndOfTitleGraceMs;
        var stalled = !draining && DateTime.UtcNow - _advanceUtc > StallThreshold;

        if (stalled && !_stallOverlayVisible)
        {
            _stallOverlayVisible = true;
            PlayerLog.Write($"stall at {position}ms, overlay shown");
            ShowBuffer(_bufferText);
        }
        else if (!stalled && _stallOverlayVisible)
        {
            _stallOverlayVisible = false;
            PlayerLog.Write($"stall over at {position}ms after {_seekWatch.ElapsedMilliseconds}ms");
            HideBuffer();
            UpdatePlayPauseUi();
        }
    }

    /// <summary>Restarts stall detection, so a deliberate seek is not a stall.</summary>
    private void ResetStallClock(long position)
    {
        _advancePositionMs = position;
        _advanceUtc = DateTime.UtcNow;
        _stallOverlayVisible = false;
    }

    // ---------------------------------------------------------------- UI actions

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void InputLayer_Tapped(object sender, TappedRoutedEventArgs e)
    {
        _engine.FocusSurface(FocusState.Pointer);

        if (_sourcesFlyoutOpen)
        {
            CloseSources();
            return;
        }

        if (_chromeHidden)
        {
            ShowChrome();
            return;
        }

        TogglePlayPause();
    }

    private void TogglePlayPause()
    {
        if (!_engine.IsReady)
        {
            return;
        }

        // Nothing chosen and nothing starting: the play button is the way into
        // the picker, which is otherwise only reachable from the transport bar.
        if (_currentSource is null && _pendingSource is null && !_errorVisible &&
            ViewModel.Sources.Count > 0)
        {
            OpenSources();
            return;
        }

        if (_ended)
        {
            if (_currentSource is not null)
            {
                PlaySource(_currentSource);
            }

            return;
        }

        if (_engine.IsPlaying)
        {
            _engine.Pause();
        }
        else
        {
            _engine.Resume();
        }

        ShowChrome();
        StartChromeTimer();
    }

    private void Back10_Click(object sender, RoutedEventArgs e) => SeekRelative(-10_000);

    private void Forward10_Click(object sender, RoutedEventArgs e) => SeekRelative(10_000);

    private void SeekRelative(long deltaMs)
    {
        if (!_engine.IsReady || _engine.DurationMs <= 0 || !_engine.IsSeekable)
        {
            return;
        }

        var target = Math.Clamp(_engine.PositionMs + deltaMs, 0, _engine.DurationMs);
        LogSeek(_engine.PositionMs, target, "skip");
        _engine.PositionMs = target;
        ShowChrome();
        StartChromeTimer();
    }

    /// <summary>
    /// Records a seek and restarts the clock the buffering log reports against,
    /// so the log says how long the engine actually stalled, not just that it
    /// did.
    /// </summary>
    private void LogSeek(long from, long to, string origin)
    {
        PlayerLog.Write(
            $"seek {origin} {from} -> {to} (len {_engine.DurationMs}, " +
            $"seekable {_engine.IsSeekable}, state {_engine.State})");
        _seekWatch.Restart();
        _bufferingLogged = false;
        ResetStallClock(to);
    }

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSeek)
        {
            return;
        }

        UpdateTimeText((long)e.NewValue, _engine.DurationMs);

        if (!_draggingSeek && _engine.IsSeekable)
        {
            _engine.PositionMs = (long)e.NewValue;
        }
    }

    private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingSeek = true;
        ShowChrome();
        _chromeTimer.Stop();
    }

    private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e) => EndSeekDrag();

    private void SeekSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndSeekDrag();

    private void EndSeekDrag()
    {
        if (!_draggingSeek)
        {
            return;
        }

        _draggingSeek = false;
        if (_engine.IsSeekable)
        {
            LogSeek(_engine.PositionMs, (long)SeekSlider.Value, "scrub");
            _engine.PositionMs = (long)SeekSlider.Value;
        }

        StartChromeTimer();
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVolume || !_engine.IsReady)
        {
            return;
        }

        var volume = (int)Math.Round(e.NewValue);
        _engine.Volume = volume;

        if (volume > 0)
        {
            _lastVolume = volume;
            if (_engine.IsMuted)
            {
                _engine.SetMuted(false);
            }
        }

        UpdateVolumeUi();
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        if (!_engine.IsReady)
        {
            return;
        }

        if (_engine.IsMuted || _engine.Volume == 0)
        {
            _engine.SetMuted(false);
            if (_engine.Volume == 0)
            {
                _engine.Volume = _lastVolume is > 0 and <= 100 ? _lastVolume : 80;
            }

            _suppressVolume = true;
            VolumeSlider.Value = _engine.Volume;
            _suppressVolume = false;
        }
        else
        {
            _lastVolume = _engine.Volume;
            _engine.SetMuted(true);
        }

        UpdateVolumeUi();
        ShowChrome();
        StartChromeTimer();
    }

    private void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag } ||
            !float.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ||
            !_engine.IsReady)
        {
            return;
        }

        _engine.SetRate(rate);
        SpeedButton.Content = $"{rate:0.##}×";
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    /// <summary>
    /// The player can be reached without the details page (resume, up next,
    /// deep link), so a missing or broken addon still has a way forward.
    /// </summary>
    private void ManageAddons_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(SettingsPage));

    private void GoBack()
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    // --------------------------------------------------------------- chrome state

    /// <summary>
    /// The full start-up overlay: artwork, logo, spinner and status. Every path
    /// that starts work on a stream goes through here, because the picker leaves
    /// the same overlay up without its spinner.
    /// </summary>
    private void ShowLoadingOverlay(string? status = null)
    {
        if (status is not null)
        {
            ViewModel.StatusText = status;
        }

        LoadingRing.Visibility = Visibility.Visible;
        LoadingOverlay.Visibility = Visibility.Visible;
        PickerBackdrop.Visibility = Visibility.Collapsed;
    }

    /// <summary>Takes the start-up artwork down once frames are on screen.</summary>
    private void HideLoadingOverlay()
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
        PickerBackdrop.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Artwork without the spinner, for the moment before a source is chosen:
    /// the picker then hangs over the poster rather than over a black surface,
    /// and nothing on screen claims that work is in flight.
    /// </summary>
    private void ShowPickerBackdrop()
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
        PickerBackdrop.Visibility = Visibility.Visible;
    }

    private void ShowChrome()
    {
        if (_disposed)
        {
            return;
        }

        var wasHidden = _chromeHidden;
        _chromeHidden = false;
        Chrome.Visibility = Visibility.Visible;
        Chrome.Opacity = 1;
        Chrome.IsHitTestVisible = true;

        if (wasHidden)
        {
            RefreshSubtitleLift();
        }
    }

    private void HideChrome()
    {
        if (!_playing || _sourcesFlyoutOpen || _subtitlesFlyoutOpen || _episodesFlyoutOpen ||
            _errorVisible || _ended || _draggingSeek)
        {
            return;
        }

        if (_keyboardActive &&
            FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused &&
            IsDescendantOf(focused, Chrome))
        {
            StartChromeTimer();
            return;
        }

        _chromeHidden = true;
        Chrome.Opacity = 0;
        Chrome.IsHitTestVisible = false;
        RefreshSubtitleLift();
    }

    /// <summary>
    /// VLC draws subtitles itself, at the bottom of the picture. While the chrome
    /// is showing they move up by whatever the control bar covers, measured from
    /// the bottom of the video rather than the window, so a letterboxed film is
    /// not lifted twice.
    /// </summary>
    private void RefreshSubtitleLift()
    {
        if (_disposed || !_engine.IsReady)
        {
            return;
        }

        var margin = _chromeHidden ? 0 : SubtitleLiftPixels();
        if (margin == _appliedSubtitleLift)
        {
            return;
        }

        _appliedSubtitleLift = margin;
        _engine.SetSubtitleLift(margin);
    }

    private int SubtitleLiftPixels()
    {
        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        var panelHeight = Root.ActualHeight;
        if (panelHeight <= 0)
        {
            return 0;
        }

        var covered = Math.Max(0, panelHeight - ControlBarTop()) * scale;
        var intrusion = covered - LetterboxHeight(panelHeight, scale);
        return intrusion <= 0
            ? 0
            : (int)Math.Round(intrusion + (SubtitleLiftGapDip * scale));
    }

    /// <summary>Top of the transport bar in the page's coordinate space.</summary>
    private double ControlBarTop()
    {
        try
        {
            return SeekSlider.TransformToVisual(Root).TransformPoint(new Point(0, 0)).Y;
        }
        catch
        {
            // The bar is not in the visual tree yet.
            return Root.ActualHeight;
        }
    }

    /// <summary>
    /// The dead space between the bottom of the window and the bottom of the
    /// picture. VLC fits the video inside the surface, so a wide film already
    /// leaves its subtitles above the bar; that gap needs no lift of its own.
    /// </summary>
    private double LetterboxHeight(double panelHeightDip, double scale)
    {
        if (!_engine.TryGetVideoSize(out var videoWidth, out var videoHeight) ||
            videoWidth == 0 ||
            videoHeight == 0)
        {
            return 0;
        }

        var panelWidth = Root.ActualWidth * scale;
        var panelHeight = panelHeightDip * scale;
        var fit = Math.Min(panelWidth / videoWidth, panelHeight / videoHeight);
        return Math.Max(0, (panelHeight - (videoHeight * fit)) / 2);
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_chromeHidden)
        {
            RefreshSubtitleLift();
        }
    }

    private void StartChromeTimer()
    {
        if (_chromeHidden || !_playing || _sourcesFlyoutOpen || _subtitlesFlyoutOpen ||
            _episodesFlyoutOpen || _errorVisible)
        {
            return;
        }

        _chromeTimer.Stop();
        _chromeTimer.Start();
    }

    private void InputLayer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _keyboardActive = false;
        if (_sourcesFlyoutOpen || _episodesFlyoutOpen || _errorVisible)
        {
            return;
        }

        ShowChrome();
        StartChromeTimer();
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _keyboardActive = false;
        if (_sourcesFlyoutOpen || _episodesFlyoutOpen || _errorVisible)
        {
            return;
        }

        ShowChrome();
        StartChromeTimer();
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _keyboardActive = false;
    }

    /// <summary>
    /// The mouse wheel over the video changes the volume, like most players.
    /// Panels that scroll keep the wheel for themselves.
    /// </summary>
    private void Root_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_sourcesFlyoutOpen || _subtitlesFlyoutOpen || _episodesFlyoutOpen || _errorVisible)
        {
            return;
        }

        var properties = e.GetCurrentPoint(Root).Properties;
        if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0)
        {
            return;
        }

        // One detent (120) is 5 points; touchpad wheels move in smaller steps.
        var step = WheelVolumeStep * (properties.MouseWheelDelta / 120.0);
        var target = Math.Clamp(
            VolumeSlider.Value + step,
            VolumeSlider.Minimum,
            VolumeSlider.Maximum);

        if (Math.Abs(target - VolumeSlider.Value) > 0.01)
        {
            VolumeSlider.Value = target;
            ShowChrome();
            StartChromeTimer();
        }

        e.Handled = true;
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        _keyboardActive = true;
        ShowChrome();
        StartChromeTimer();
    }

    /// <summary>
    /// Player-wide media shortcuts. PreviewKeyDown runs before the focused
    /// control, so Space doesn't activate the focused button and arrows don't
    /// move focus. The volume slider keeps its normal arrow behaviour.
    /// </summary>
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_sourcesFlyoutOpen || _subtitlesFlyoutOpen || _episodesFlyoutOpen)
        {
            return;
        }

        var focused = FocusManager.GetFocusedElement(XamlRoot);
        var volumeFocused = ReferenceEquals(focused, VolumeSlider);

        switch (e.Key)
        {
            case VirtualKey.Space:
                if (e.KeyStatus.WasKeyDown)
                {
                    e.Handled = true;
                    break;
                }

                TogglePlayPause();
                e.Handled = true;
                break;

            case VirtualKey.Left:
                if (volumeFocused)
                {
                    return;
                }

                SeekRelative(-10_000);
                e.Handled = true;
                break;

            case VirtualKey.Right:
                if (volumeFocused)
                {
                    return;
                }

                SeekRelative(10_000);
                e.Handled = true;
                break;

            // OEM brackets: nudge subtitle timing while watching. Repeated key
            // events are welcome here, so they are not filtered like Space.
            case (VirtualKey)0xDB: // [
                if (HasActiveSubtitle)
                {
                    AdjustSubtitleDelay(-SubtitleDelayStepMs);
                    e.Handled = true;
                }

                break;

            case (VirtualKey)0xDD: // ]
                if (HasActiveSubtitle)
                {
                    AdjustSubtitleDelay(SubtitleDelayStepMs);
                    e.Handled = true;
                }

                break;
        }
    }

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        var current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    // -------------------------------------------------------------- source picker

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.SourceTabs) or nameof(PlayerViewModel.SelectedSourceTab))
        {
            SyncSourceTabs();
        }
    }

    /// <summary>
    /// SelectorBar has no ItemsSource, so its items follow the view model by
    /// hand. Rebuilding the bar on every notification also survives addon
    /// answers that arrive one by one while the flyout is open.
    /// </summary>
    private void SyncSourceTabs()
    {
        _syncingSourceTabs = true;
        try
        {
            SourceTabBar.Items.Clear();

            foreach (var tab in ViewModel.SourceTabs)
            {
                SourceTabBar.Items.Add(new SelectorBarItem
                {
                    Text = tab.Title,
                    Tag = tab,
                });
            }

            SourceTabBar.SelectedItem =
                SourceTabBar.Items.FirstOrDefault(item => ReferenceEquals(item.Tag, ViewModel.SelectedSourceTab))
                ?? SourceTabBar.Items.FirstOrDefault();
        }
        finally
        {
            _syncingSourceTabs = false;
        }
    }

    private void SourceTabBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingSourceTabs)
        {
            return;
        }

        if (sender.SelectedItem?.Tag is SourceTab tab)
        {
            ViewModel.SelectedSourceTab = tab;

            // A shorter list keeps the old offset otherwise, opening the new
            // tab halfway down.
            VisualTreeHelpers.FindDescendant<ScrollViewer>(SourceList)?.ChangeView(null, 0, null, true);
        }
    }

    /// <summary>
    /// Opens the native flyout anchored to the transport-bar source button. It
    /// light dismisses, closes on Escape and moves focus into the list by itself,
    /// and the video keeps playing behind it while the user compares streams.
    /// </summary>
    private void OpenSources()
    {
        if (ViewModel.Sources.Count == 0)
        {
            return;
        }

        if (SourcesFlyout.IsOpen)
        {
            return;
        }

        if (!IsLoaded)
        {
            _sourcesFlyoutPending = true;
            return;
        }

        // The flyout is anchored to the button, so the chrome must not fade out
        // from under its anchor while the picker is up.
        _chromeTimer.Stop();
        ShowChrome();
        SourcesFlyout.ShowAt(SourcesButton);
    }

    private void SourcesFlyout_Opened(object? sender, object e)
    {
        _sourcesFlyoutOpen = true;
        _chromeTimer.Stop();
        ShowChrome();

        // The tab bar is rebuilt by hand, and the previous session may have left
        // a scroll offset behind. Do both before the list is focused.
        SyncSourceTabs();
        VisualTreeHelpers.FindDescendant<ScrollViewer>(SourceList)?.ChangeView(null, 0, null, true);
    }

    private void SourcesFlyout_Closed(object? sender, object e)
    {
        _sourcesFlyoutOpen = false;
        ShowChrome();
        if (_playing)
        {
            StartChromeTimer();
        }
    }

    /// <summary>Closes the picker if the flyout didn't get the message itself.</summary>
    private void CloseSources()
    {
        if (SourcesFlyout.IsOpen)
        {
            SourcesFlyout.Hide();
        }
    }

    private void SourceList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PlaybackSource source)
        {
            return;
        }

        SourcesFlyout.Hide();

        if (_currentSource?.Url.Equals(source.Url, StringComparison.OrdinalIgnoreCase) == true &&
            !_errorVisible)
        {
            return;
        }

        PlaySource(source);
    }

    // ---------------------------------------------------------------- episodes

    private void OpenEpisodes_Click(object sender, RoutedEventArgs e) =>
        OpenEpisodes(sender as FrameworkElement);

    private void CloseEpisodes_Click(object sender, RoutedEventArgs e) => EpisodesFlyout.Hide();

    /// <summary>
    /// Opens the native flyout anchored to the Episodes button. It light
    /// dismisses, keeps focus inside the list and closes on Escape by itself;
    /// the chrome timer is paused so the controls don't disappear underneath it.
    /// </summary>
    private void OpenEpisodes(FrameworkElement? anchor = null)
    {
        if (!ViewModel.CanBrowseEpisodes || EpisodesFlyout.IsOpen)
        {
            return;
        }

        _chromeTimer.Stop();
        EpisodesFlyout.ShowAt(anchor ?? EpisodesButton);
    }

    private void EpisodesFlyout_Opened(object? sender, object e)
    {
        _episodesFlyoutOpen = true;
        _chromeTimer.Stop();
        ShowChrome();
        EpisodeList.Focus(FocusState.Programmatic);
    }

    private void EpisodesFlyout_Closed(object? sender, object e)
    {
        _episodesFlyoutOpen = false;
        ShowChrome();
        if (_playing)
        {
            StartChromeTimer();
        }
    }

    private void EpisodeList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PlayerEpisode episode)
        {
            return;
        }

        EpisodesFlyout.Hide();

        if (ViewModel.Request is { } current &&
            current.Season == episode.Season &&
            current.Episode == episode.EpisodeNumber &&
            !_errorVisible)
        {
            return;
        }

        _ = PlayEpisodeAsync(episode.Video);
    }

    private void EpisodesAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!ViewModel.CanBrowseEpisodes ||
            _sourcesFlyoutOpen ||
            _subtitlesFlyoutOpen ||
            EpisodesFlyout.IsOpen)
        {
            return;
        }

        OpenEpisodes();
        args.Handled = true;
    }

    private void SubtitlesFlyout_Opened(object? sender, object e)
    {
        _subtitlesFlyoutOpen = true;
        _chromeTimer.Stop();
        ShowChrome();

        // Tracks parse while the panel is closed, so read them fresh each time.
        RefreshEmbeddedSubtitles();
        ShowSubtitleLanguages();
        RefreshSubtitleFlyout();
    }

    private void SubtitlesFlyout_Closed(object? sender, object e)
    {
        _subtitlesFlyoutOpen = false;
        ShowChrome();
        if (_playing)
        {
            StartChromeTimer();
        }
    }

    // ------------------------------------------------------- subtitle timing

    /// <summary>A subtitle is on screen, so timing is worth showing and adjusting.</summary>
    private bool HasActiveSubtitle =>
        _selectedSubtitle is not null || _selectedEmbeddedTrackId >= 0;

    /// <summary>
    /// Timing belongs to the title, not one episode or one source: a release
    /// pack shares its offset across episodes, and a source switch must not
    /// make the user find the same number again.
    /// </summary>
    private static string SubtitleOffsetKey(PlaybackRequest request) =>
        string.IsNullOrWhiteSpace(request.Meta.Id) ? request.VideoId : request.Meta.Id;

    private async Task RestoreSubtitleDelayAsync(PlaybackRequest request)
    {
        _subtitleDelayMs = await App.SubtitleOffsets.GetDelayAsync(SubtitleOffsetKey(request));
        ApplySubtitleDelay();
        UpdateSubtitleTimingUi();
    }

    private void AdjustSubtitleDelay(long deltaMs) =>
        SetSubtitleDelay(_subtitleDelayMs + deltaMs, announce: true);

    private void SetSubtitleDelay(long delayMs, bool announce)
    {
        _subtitleDelayMs = Math.Clamp(delayMs, -SubtitleDelayMaxMs, SubtitleDelayMaxMs);
        ApplySubtitleDelay();
        UpdateSubtitleTimingUi();

        if (announce)
        {
            ShowSubtitleTimingHud();
        }

        if (ViewModel.Request is { } request)
        {
            _ = App.SubtitleOffsets.SetDelayAsync(SubtitleOffsetKey(request), _subtitleDelayMs);
        }
    }

    /// <summary>
    /// The engine resets subtitle timing for every new media, so this is
    /// reapplied on each source start as well as each change.
    /// </summary>
    private void ApplySubtitleDelay()
    {
        try
        {
            _engine.SetSubtitleDelay(_subtitleDelayMs);
        }
        catch
        {
            // Timing is best effort; playback continues either way.
        }
    }

    private void UpdateSubtitleTimingUi()
    {
        SubtitleTimingPanel.Visibility = HasActiveSubtitle
            ? Visibility.Visible
            : Visibility.Collapsed;

        SubtitleTimingValue.Text = FormatSubtitleDelayValue(_subtitleDelayMs);
        SubtitleTimingHint.Text = _subtitleDelayMs switch
        {
            0 => "Subtitles are in sync",
            > 0 => $"Subtitles appear {FormatSubtitleDelaySeconds(_subtitleDelayMs)} later",
            _ => $"Subtitles appear {FormatSubtitleDelaySeconds(_subtitleDelayMs)} earlier",
        };
        SubtitleResetButton.Visibility = _subtitleDelayMs == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static string FormatSubtitleDelaySeconds(long delayMs) =>
        $"{Math.Abs(delayMs) / 1000.0:0.0#} s";

    private static string FormatSubtitleDelayValue(long delayMs) => delayMs switch
    {
        0 => "0.0 s",
        > 0 => $"+{FormatSubtitleDelaySeconds(delayMs)}",
        _ => $"\u2212{FormatSubtitleDelaySeconds(delayMs)}",
    };

    /// <summary>
    /// A short toast over the picture, so a keyboard adjustment is visible
    /// without opening the panel or waking the whole chrome.
    /// </summary>
    private void ShowSubtitleTimingHud()
    {
        SubtitleHudText.Text = _subtitleDelayMs == 0
            ? "Subtitle timing reset"
            : $"Subtitle timing · {FormatSubtitleDelaySeconds(_subtitleDelayMs)} {(_subtitleDelayMs > 0 ? "later" : "earlier")}";
        SubtitleHudPanel.Visibility = Visibility.Visible;
        _subtitleHudTimer?.Stop();
        _subtitleHudTimer?.Start();
    }

    private void SubtitleEarlier_Click(object sender, RoutedEventArgs e) =>
        AdjustSubtitleDelay(-SubtitleDelayStepMs);

    private void SubtitleLater_Click(object sender, RoutedEventArgs e) =>
        AdjustSubtitleDelay(SubtitleDelayStepMs);

    private void SubtitleReset_Click(object sender, RoutedEventArgs e) =>
        SetSubtitleDelay(0, announce: true);

    // ------------------------------------------------- embedded subtitle tracks

    private async void SubtitleLanguageList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SubtitleLanguageChoice language)
        {
            return;
        }

        if (language.IsOff)
        {
            if (await ApplySelectedSubtitleAsync(null))
            {
                SubtitlesFlyout.Hide();
            }

            return;
        }

        if (language.IsEmbedded)
        {
            SelectEmbeddedSubtitle(language, closeFlyout: true);
            return;
        }

        if (language.Versions.Count == 1)
        {
            var previous = _selectedSubtitle;
            if (await ApplySelectedSubtitleAsync(language.Versions[0]))
            {
                SubtitlesFlyout.Hide();
            }
            else
            {
                RestoreSubtitleSelection(previous);
            }

            return;
        }

        ShowSubtitleVersions(language);
    }

    private void ShowSubtitleLanguages()
    {
        _versionsLanguage = null;
        _suppressSubtitleSelection = true;
        SubtitleVersionList.SelectedItem = null;
        SubtitleVersionList.ItemsSource = null;
        _suppressSubtitleSelection = false;
        SubtitleVersionView.Visibility = Visibility.Collapsed;
        SubtitleLanguageView.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Reads the subtitle tracks muxed into the current stream. They need no
    /// download and are usually timed for the picture, so they belong at the
    /// top of the list, right under Off.
    /// </summary>
    private void RefreshEmbeddedSubtitles()
    {
        var tracks = new List<SubtitleLanguageChoice>();

        try
        {
            foreach (var track in _engine.SubtitleTracks)
            {
                if (track.Id < 0 || string.IsNullOrWhiteSpace(track.DisplayName))
                {
                    continue;
                }

                tracks.Add(new SubtitleLanguageChoice(
                    track.DisplayName,
                    track.LanguageCode ?? "",
                    isOff: false,
                    [],
                    "Embedded in this video",
                    track.Id));
            }
        }
        catch
        {
            // A stream may disappear between reading the id and its tracks.
        }

        ViewModel.SetEmbeddedSubtitles(tracks);

        // A source switch re-enumerates tracks; keep the language the viewer
        // already picked instead of silently dropping to Off. Once the new
        // stream has exposed its tracks, stop waiting for a match.
        if (tracks.Count > 0 && _selectedEmbeddedTrackId < 0 && !string.IsNullOrEmpty(_selectedEmbeddedCode))
        {
            var match = tracks.FirstOrDefault(track =>
                SubtitleLanguages.SameLanguage(track.LanguageCode, _selectedEmbeddedCode));
            if (match is not null)
            {
                SelectEmbeddedSubtitle(match, closeFlyout: false);
            }
            else
            {
                _selectedEmbeddedCode = null;
            }
        }
    }

    private bool SelectEmbeddedSubtitle(SubtitleLanguageChoice track, bool closeFlyout)
    {
        if (!_engine.IsReady || track.EmbeddedTrackId < 0)
        {
            return false;
        }

        if (!_engine.SelectSubtitleTrack(track.EmbeddedTrackId))
        {
            return false;
        }

        _selectedEmbeddedTrackId = track.EmbeddedTrackId;
        _selectedEmbeddedName = track.LanguageName;
        _selectedEmbeddedCode = track.LanguageCode;
        _selectedSubtitle = null;
        _selectedSubtitlePath = null;
        _subtitleApplied = true;
        ViewModel.SubtitleStatus = "";

        ViewModel.SelectSubtitle(null);
        UpdateSubtitleIndicator();
        ApplySubtitleDelay();
        UpdateSubtitleCurrent();
        UpdateSubtitleTimingUi();

        if (closeFlyout)
        {
            SubtitlesFlyout.Hide();
        }

        return true;
    }

    private void ShowSubtitleVersions(SubtitleLanguageChoice language)
    {
        _versionsLanguage = language;

        SubtitleVersionTitle.Text = language.LanguageName;
        SubtitleVersionHint.Text = _selectedSubtitle is not null &&
                                   _selectedSubtitle.LanguageCode == language.LanguageCode
            ? $"Current: {_selectedSubtitle.Label}"
            : "Choose the version that matches your release.";
        SubtitleVersionList.ItemsSource = language.Versions;

        _suppressSubtitleSelection = true;
        SubtitleVersionList.SelectedItem = _selectedSubtitle is not null &&
                                           _selectedSubtitle.LanguageCode == language.LanguageCode
            ? _selectedSubtitle
            : null;
        _suppressSubtitleSelection = false;

        SubtitleLanguageView.Visibility = Visibility.Collapsed;
        SubtitleVersionView.Visibility = Visibility.Visible;
    }

    private void SubtitleVersionBack_Click(object sender, RoutedEventArgs e)
    {
        ShowSubtitleLanguages();
        RefreshSubtitleFlyout();
    }

    private async void SubtitleVersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSubtitleSelection ||
            SubtitleVersionList.SelectedItem is not SubtitleVersionChoice version)
        {
            return;
        }

        var previous = _selectedSubtitle;

        if (await ApplySelectedSubtitleAsync(version))
        {
            SubtitlesFlyout.Hide();
            return;
        }

        RestoreSubtitleSelection(previous);
        _suppressSubtitleSelection = true;
        SubtitleVersionList.SelectedItem = previous is not null &&
                                           previous.LanguageCode == version.LanguageCode
            ? previous
            : null;
        _suppressSubtitleSelection = false;
    }

    private void RestoreSubtitleSelection(SubtitleVersionChoice? version)
    {
        _selectedSubtitle = version;
        ViewModel.SelectSubtitle(version);
        UpdateSubtitleIndicator();
        UpdateSubtitleCurrent();
        UpdateSubtitleTimingUi();
        RefreshSubtitleFlyout();
    }

    /// <summary>
    /// Downloads and attaches a subtitle track. Auto-selection passes
    /// <paramref name="applyNow"/> false so a track chosen before playback
    /// starts is attached by the Playing handler instead of failing against an
    /// empty player, and <paramref name="remember"/> false so starting up does
    /// not rewrite the choice that was just read.
    /// </summary>
    private async Task<bool> ApplySelectedSubtitleAsync(
        SubtitleVersionChoice? version,
        bool applyNow = true,
        bool remember = true)
    {
        _selectedSubtitle = version;
        ViewModel.SelectSubtitle(version);
        UpdateSubtitleIndicator();

        if (version is null)
        {
            _selectedSubtitlePath = null;
            _selectedEmbeddedTrackId = -1;
            _selectedEmbeddedName = null;
            _selectedEmbeddedCode = null;
            _engine.SelectSubtitleTrack(-1);
            _subtitleApplied = true;
            ViewModel.SubtitleStatus = "";
            UpdateSubtitleCurrent();
            UpdateSubtitleTimingUi();
            return true;
        }

        if (string.IsNullOrWhiteSpace(version.Url))
        {
            ViewModel.SubtitleStatus = "That subtitle has no download link.";
            return false;
        }

        ViewModel.SubtitleStatus = $"Loading {version.LanguageName} · {version.Label}…";
        UpdateSubtitleCurrent();
        RefreshSubtitleFlyout();

        var path = await DownloadSubtitleAsync(version.Url);
        if (_disposed)
        {
            return false;
        }

        if (path is null)
        {
            ViewModel.SubtitleStatus = "Couldn't download that subtitle.";
            return false;
        }

        _selectedSubtitlePath = path;
        // The addon file replaces any embedded track that was active.
        _selectedEmbeddedTrackId = -1;
        _selectedEmbeddedName = null;
        _selectedEmbeddedCode = null;
        ViewModel.SubtitleStatus = "";
        UpdateSubtitleCurrent();
        UpdateSubtitleTimingUi();

        if (remember)
        {
            RememberSubtitleLanguage(version);
        }

        if (!applyNow && _currentSource is null)
        {
            // The Playing handler attaches the downloaded track once a source starts.
            return true;
        }

        return ApplyLocalSubtitle(path);
    }

    /// <summary>
    /// Keeps the language the viewer picked for this movie or series, without
    /// touching the app-wide preferred language. A series shares its id with
    /// every episode, so the next episode keeps the same language. Picking the
    /// preference again clears the override, and choosing Off is deliberately
    /// not remembered.
    /// </summary>
    private void RememberSubtitleLanguage(SubtitleVersionChoice version)
    {
        if (ViewModel.Request is not { } request)
        {
            return;
        }

        var code = SubtitleLanguages.CanonicalCode(version.LanguageCode);
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var titleId = SubtitleOffsetKey(request);
        var preferred = SubtitleLanguages.CanonicalCode(App.Settings.PreferredSubtitleLanguage);

        if (code.Equals(preferred, StringComparison.OrdinalIgnoreCase))
        {
            _ = App.SubtitleChoices.ClearLanguageAsync(titleId);
        }
        else
        {
            _ = App.SubtitleChoices.SetLanguageAsync(titleId, code);
        }
    }

    private string EnsureSubtitleDirectory()
    {
        if (_subtitleDirectory is null)
        {
            string baseDirectory;

            try
            {
                baseDirectory = ApplicationData.Current.TemporaryFolder.Path;
            }
            catch
            {
                // Unpackaged builds have no package identity.
                baseDirectory = Path.GetTempPath();
            }

            _subtitleDirectory = Path.Combine(
                baseDirectory,
                "PirsaSubtitles",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_subtitleDirectory);
        }

        return _subtitleDirectory;
    }

    /// <summary>
    /// VLC 3.0.24's GnuTLS plugin can crash when remote subtitle downloads
    /// overlap with stream TLS sessions. Download the subtitle with .NET and
    /// hand VLC a local file instead.
    /// </summary>
    private async Task<string?> DownloadSubtitleAsync(string url)
    {
        try
        {
            using var response = await SubtitleHttp.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
            {
                return null;
            }

            var directory = EnsureSubtitleDirectory();
            var extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
            if (extension is not (".srt" or ".vtt" or ".ass" or ".ssa" or ".sub"))
            {
                extension = ".srt";
            }

            var path = Path.Combine(directory, $"{Guid.NewGuid():N}{extension}");
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private bool ApplyLocalSubtitle(string path)
    {
        if (!_engine.IsReady)
        {
            // The Playing handler attaches the downloaded track once a source starts.
            return true;
        }

        if (!_engine.AddSubtitleFile(path))
        {
            ViewModel.SubtitleStatus = "Couldn't load that subtitle.";
            return false;
        }

        _subtitleApplied = true;
        // Adding a subtitle track can reset the input's timing; put the saved offset back.
        ApplySubtitleDelay();
        return true;
    }

    private static HttpClient CreateSubtitleHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Pirsa/1.0");
        return client;
    }

    private void NextSource_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Sources.Count == 0)
        {
            return;
        }

        var index = 0;
        if (_currentSource is not null)
        {
            var current = ViewModel.Sources
                .Select((source, i) => (source, i))
                .FirstOrDefault(item => item.source.Url.Equals(_currentSource.Url, StringComparison.OrdinalIgnoreCase));
            index = current == default ? 0 : (current.i + 1) % ViewModel.Sources.Count;
        }

        PlaySource(ViewModel.Sources[index]);
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSource is not null)
        {
            PlaySource(_currentSource);
            return;
        }

        if (ViewModel.Request is { } request)
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            _errorVisible = false;
            ShowLoadingOverlay("Finding sources…");
            _ = LoadSourcesAsync(request);
        }
    }

    // ------------------------------------------------------------------- fullscreen

    private void ToggleFullscreen() => SetFullscreen(!_isFullScreen);

    private void SetFullscreen(bool fullscreen)
    {
        _isFullScreen = fullscreen;

        if (App.Window is MainWindow shell)
        {
            shell.SetPlayerFullscreen(fullscreen);
        }

        OverlayBackButton.Visibility = fullscreen ? Visibility.Visible : Visibility.Collapsed;
        FullScreenIcon.Glyph = fullscreen ? "\uE73F" : "\uE740";
        AutomationProperties.SetName(FullScreenButton, fullscreen ? "Exit full screen" : "Full screen");
        ToolTipService.SetToolTip(FullScreenButton, fullscreen ? "Exit full screen (F)" : "Full screen (F)");
        ShowChrome();
        StartChromeTimer();
    }

    private void FullscreenAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleFullscreen();
        args.Handled = true;
    }

    private void MuteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleMute();
        args.Handled = true;
    }

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_isFullScreen)
        {
            SetFullscreen(false);
            args.Handled = true;
        }
        else if (_sourcesFlyoutOpen)
        {
            CloseSources();
            args.Handled = true;
        }
        else if (_subtitlesFlyoutOpen)
        {
            SubtitlesFlyout.Hide();
            args.Handled = true;
        }
        else if (_episodesFlyoutOpen)
        {
            EpisodesFlyout.Hide();
            args.Handled = true;
        }
    }

    // ---------------------------------------------------------------- small helpers

    private void ShowBuffer(string text)
    {
        _bufferText = text;
        BufferText.Text = text;
        BufferRing.IsActive = true;
        BufferPanel.Visibility = Visibility.Visible;
        CenterPlayButton.Visibility = Visibility.Collapsed;
        EndedText.Visibility = Visibility.Collapsed;
    }

    private void HideBuffer()
    {
        BufferRing.IsActive = false;
        BufferPanel.Visibility = Visibility.Collapsed;
    }

    private void UpdatePlayPauseUi()
    {
        var glyph = _playing ? "\uE769" : _ended ? "\uE72C" : "\uE768";
        PlayPauseIcon.Glyph = glyph;
        CenterPlayIcon.Glyph = glyph;
        CenterPlayIcon.FontSize = _ended ? 26 : 34;

        var name = _playing ? "Pause" : _ended ? "Replay" : "Play";
        AutomationProperties.SetName(PlayPauseButton, name);
        AutomationProperties.SetName(CenterPlayButton, name);

        CenterPlayButton.Visibility = _playing || _errorVisible || _sourcesFlyoutOpen || _disposed
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateVolumeUi()
    {
        if (!_engine.IsReady)
        {
            return;
        }

        var muted = _engine.IsMuted || _engine.Volume == 0;
        VolumeIcon.Glyph = muted ? "\uE74F" : "\uE767";
        AutomationProperties.SetName(VolumeButton, muted ? "Unmute" : "Mute");
        ToolTipService.SetToolTip(VolumeButton, muted ? "Unmute (M)" : "Mute (M)");
    }

    private void UpdateTimeText(long time, long length)
    {
        TimeText.Text = $"{FormatTime(time)} / {FormatTime(length)}";
    }

    private static string FormatTime(long milliseconds)
    {
        if (milliseconds <= 0)
        {
            return "0:00";
        }

        var time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private void ShowPlaybackError(string message, bool canSwitch)
    {
        _errorVisible = true;
        _playing = false;
        _switching = false;
        _stallTimer.Stop();
        HideBuffer();
        HideLoadingOverlay();
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        RetryButton.IsEnabled = true;
        NextSourceButton.IsEnabled = canSwitch && ViewModel.Sources.Count > 1;
        CenterPlayButton.Visibility = Visibility.Collapsed;
        EndedText.Visibility = Visibility.Collapsed;
        ShowChrome();
        _chromeTimer.Stop();
    }

    private void DisposePlayback()
    {
        if (_disposed)
        {
            return;
        }

        if (!_ended)
        {
            PersistProgress(_positionMs, _lengthMs);
        }

        _disposed = true;
        _chromeTimer.Stop();
        _resumeHintTimer?.Stop();
        _subtitleHudTimer?.Stop();
        _cachedWatchdog?.Stop();
        _stallTimer.Stop();
        _streamCacheCts.Cancel();
        var cacheSession = _streamCache;
        _streamCache = null;
        if (cacheSession is not null)
        {
            _ = cacheSession.DisposeAsync().AsTask();
        }

        _nextEpisodeCts?.Cancel();
        _nextEpisodeCts?.Dispose();
        _nextEpisodeCts = null;
        _sourceCts?.Cancel();
        _sourceCts?.Dispose();
        _sourceCts = null;
        ViewModel.Detach();
        _subtitleCts?.Cancel();
        _subtitleCts?.Dispose();
        _subtitleCts = null;

        // A timing change made just before leaving must reach the next session.
        _ = App.SubtitleOffsets.FlushAsync();

        if (_subtitleDirectory is not null)
        {
            try
            {
                Directory.Delete(_subtitleDirectory, recursive: true);
            }
            catch
            {
            }

            _subtitleDirectory = null;
        }

        // Detach the surface before the engine tears its swap chain down, so
        // XAML never presents a dead surface.
        VideoHost.Children.Remove(_engine.Surface);
        _engine.Dispose();
    }
}

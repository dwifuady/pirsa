using System.Globalization;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.Helpers;
using Pirsa.Models;
using Pirsa.Services;
using Pirsa.ViewModels;
using Windows.System;
using Windows.Storage;

namespace Pirsa.Views;

/// <summary>
/// Native-feeling video player built on LibVLC's WinUI SwapChainPanel view.
/// VLC handles the container/codec variety these addons throw at it (MKV,
/// HEVC, HDR, HLS, DASH) while the chrome is WinUI.
/// </summary>
public sealed partial class PlayerPage : Page
{
    private static readonly object CoreLock = new();
    private static bool _coreReady;
    private static readonly HttpClient SubtitleHttp = CreateSubtitleHttpClient();

    /// <summary>Volume points changed per mouse-wheel detent.</summary>
    private const double WheelVolumeStep = 5;

    private readonly PlayerViewModel _viewModel = new(App.Addons, App.StreamDiscovery);
    private readonly DispatcherTimer _chromeTimer;

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _media;
    private CancellationTokenSource? _sourceCts;
    private CancellationTokenSource? _subtitleCts;
    private PlaybackSource? _pendingSource;
    private PlaybackSource? _currentSource;
    private SubtitleVersionChoice? _selectedSubtitle;
    private SubtitleLanguageChoice? _versionsLanguage;
    private string? _selectedSubtitlePath;
    private string? _subtitleDirectory;

    private bool _disposed;
    private bool _vlcReady;
    private bool _playing;
    private bool _ended;
    private bool _switching;
    private bool _errorVisible;
    private bool _sourcePanelOpen;
    private bool _subtitlesFlyoutOpen;
    private bool _subtitleApplied;
    private bool _suppressSubtitleSelection;
    private bool _draggingSeek;
    private bool _suppressSeek;
    private bool _suppressVolume;
    private List<TrackDescriptionTile> _audioTracks = new();
    private int _selectedAudioTrackId = -1;
    private bool _audioListDirty = true;
    private bool _chromeHidden = true;
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
    private Task<bool>? _sourceDiscovery;
    private CancellationTokenSource? _nextEpisodeCts;
    private MetaItem? _seriesMeta;
    private MetaVideo? _nextEpisode;
    private bool _upNextDismissed;
    private bool _advancing;
    private int _lastVolume = 80;

    public PlayerViewModel ViewModel => _viewModel;

    public PlayerPage()
    {
        InitializeComponent();

        _chromeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _chromeTimer.Tick += (_, _) =>
        {
            _chromeTimer.Stop();
            HideChrome();
        };

        _resumeHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _resumeHintTimer.Tick += (_, _) => HideResumeHint();

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

        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SeekSlider_PointerPressed), true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SeekSlider_PointerReleased), true);
        SeekSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(SeekSlider_PointerCaptureLost), true);
        Root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Root_PointerMoved), true);
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Root_PointerPressed), true);
        Root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Root_PointerWheelChanged), true);
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Root_KeyDown), true);
        Root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(Root_PreviewKeyDown), true);

        Loaded += (_, _) => VideoSurface.Focus(FocusState.Programmatic);

        VolumeSlider.Value = 80;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not PlaybackRequest request)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                ShowPlaybackError("This title is missing playback information.", canSwitch: false);
            });
            return;
        }

        ViewModel.Request = request;
        _selectedSubtitle = null;
        _resumeFromHistoryMs = 0;
        _positionMs = 0;
        _lengthMs = 0;
        _ended = false;
        ResumePanel.Visibility = Visibility.Collapsed;
        HideUpNext();
        _upNextDismissed = false;
        ViewModel.NextEpisode = null;
        _ = LoadSourcesAsync(request);
        _ = LoadSubtitlesAsync(request);
        _ = LoadNextEpisodeAsync(request);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        DisposePlayback();
        if (App.Window is MainWindow shell)
        {
            shell.SetPlayerFullscreen(false);
        }
    }

    // ---------------------------------------------------------------- VLC setup

    private async void VideoSurface_Initialized(object? sender, EventArgs e)
    {
        try
        {
            await VideoEngine.WarmUpAsync();
            if (_disposed)
            {
                return;
            }

            EnsureCoreInitialized();

            var options = new List<string>(VideoSurface.SwapChainOptions)
            {
                "--no-video-title-show",
                "--no-osd",
            };

            _libVlc = new LibVLC([.. options]);
            _mediaPlayer = new MediaPlayer(_libVlc)
            {
                Volume = 80,
            };

            HookMediaPlayer();
            VideoSurface.MediaPlayer = _mediaPlayer;
            _vlcReady = true;
            VideoSurface.Focus(FocusState.Programmatic);
            TryStartPlayback();
        }
        catch (Exception exception)
        {
            ShowPlaybackError($"The video engine couldn't start. {exception.Message}", canSwitch: false);
        }
    }

    private static void EnsureCoreInitialized()
    {
        lock (CoreLock)
        {
            if (_coreReady)
            {
                return;
            }

            Core.Initialize();
            _coreReady = true;
        }
    }

    private void HookMediaPlayer()
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        _mediaPlayer.Playing += MediaPlayer_Playing;
        _mediaPlayer.Paused += MediaPlayer_Paused;
        _mediaPlayer.Stopped += MediaPlayer_Stopped;
        _mediaPlayer.EndReached += MediaPlayer_EndReached;
        _mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
        _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
        _mediaPlayer.LengthChanged += MediaPlayer_LengthChanged;
        _mediaPlayer.Buffering += MediaPlayer_Buffering;
        _mediaPlayer.ESAdded += MediaPlayer_ESAdded;
        _mediaPlayer.ESDeleted += MediaPlayer_ESDeleted;
        _mediaPlayer.ESSelected += MediaPlayer_ESSelected;
    }

    private void UnhookMediaPlayer()
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        _mediaPlayer.Playing -= MediaPlayer_Playing;
        _mediaPlayer.Paused -= MediaPlayer_Paused;
        _mediaPlayer.Stopped -= MediaPlayer_Stopped;
        _mediaPlayer.EndReached -= MediaPlayer_EndReached;
        _mediaPlayer.EncounteredError -= MediaPlayer_EncounteredError;
        _mediaPlayer.TimeChanged -= MediaPlayer_TimeChanged;
        _mediaPlayer.LengthChanged -= MediaPlayer_LengthChanged;
        _mediaPlayer.Buffering -= MediaPlayer_Buffering;
        _mediaPlayer.ESAdded -= MediaPlayer_ESAdded;
        _mediaPlayer.ESDeleted -= MediaPlayer_ESDeleted;
        _mediaPlayer.ESSelected -= MediaPlayer_ESSelected;
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
            await ViewModel.LoadSubtitlesAsync(request, token);

            // Subtitles for the configured language turn on without opening
            // the picker, unless something was already selected for this title.
            if (!_disposed &&
                !token.IsCancellationRequested &&
                _selectedSubtitle is null &&
                ViewModel.FindSubtitleForLanguage(App.Settings.PreferredSubtitleLanguage) is { } preferred)
            {
                // Playback may not have started yet; the track is attached when it does.
                if (!await ApplySelectedSubtitleAsync(preferred, applyNow: false))
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
        SubtitleLanguageList.ItemsSource ??= ViewModel.SubtitleMenu;

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
    }

    private void UpdateSubtitleCurrent()
    {
        if (_selectedSubtitle is null)
        {
            SubtitleCurrentText.Text = "";
            SubtitleCurrentText.Visibility = Visibility.Collapsed;
        }
        else
        {
            SubtitleCurrentText.Text = $"Current: {_selectedSubtitle.LanguageName} · {_selectedSubtitle.Label}";
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
            LoadingOverlay.Visibility = Visibility.Collapsed;
            SourcesButton.IsEnabled = false;
            ShowPlaybackError(ViewModel.ErrorMessage, canSwitch: false);
            return;
        }

        LoadingOverlay.Visibility = Visibility.Collapsed;
        SourcesButton.IsEnabled = true;
        ShowChrome();

        var autoplay = SelectAutoplaySource(force: autoStart);
        if (autoplay is not null)
        {
            _pendingSource = autoplay;
            TryStartPlayback();
            return;
        }

        // Default: let the user pick a source. Grouped by addon.
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
        try
        {
            _mediaPlayer?.Stop();
        }
        catch
        {
        }

        _media?.Dispose();
        _media = null;
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
        LoadingOverlay.Visibility = Visibility.Visible;
        ViewModel.StatusText = "That saved source didn't work. Finding another…";

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
        LoadingOverlay.Visibility = Visibility.Collapsed;
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

        if (_mediaPlayer is { IsSeekable: true } player && _currentSource is not null)
        {
            player.Time = 0;
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
    private async Task PlayNextEpisodeAsync()
    {
        if (_advancing || _disposed)
        {
            return;
        }

        var next = _nextEpisode;
        var series = _seriesMeta;
        if (next is null || series is null)
        {
            return;
        }

        _advancing = true;
        try
        {
            // Save the finished (or skipped) episode before the request changes.
            PersistProgress(_positionMs, _lengthMs);

            var request = PlaybackRequest.ForEpisode(series, next);

            _nextEpisode = null;
            _upNextDismissed = false;
            ViewModel.NextEpisode = null;
            HideUpNext();

            PrepareNextEpisode();

            ViewModel.Request = request;
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
        _switching = true;

        try
        {
            _mediaPlayer?.Stop();
        }
        catch
        {
        }

        _media?.Dispose();
        _media = null;
        _currentSource = null;

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
        SubtitleActiveDot.Visibility = Visibility.Collapsed;
        ViewModel.SelectSubtitle(null);
        ViewModel.SubtitleStatus = "";

        if (SubtitlesFlyout.IsOpen)
        {
            SubtitlesFlyout.Hide();
        }

        if (_sourcePanelOpen)
        {
            CloseSources();
        }

        _resumeHintTimer?.Stop();
        ResumePanel.Visibility = Visibility.Collapsed;
        EndedText.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        HideBuffer();
        HideUpNext();
        LoadingOverlay.Visibility = Visibility.Visible;
        UpdatePlayPauseUi();
        ShowChrome();
    }

    private void TryStartPlayback()
    {
        if (_disposed || !_vlcReady || _pendingSource is null)
        {
            return;
        }

        var source = _pendingSource;
        _pendingSource = null;
        PlaySource(source);
    }

    private void PlaySource(PlaybackSource source)
    {
        if (_mediaPlayer is null || _libVlc is null)
        {
            _pendingSource = source;
            return;
        }

        try
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            _errorVisible = false;

            var resume = !_ended &&
                         _mediaPlayer.State is VLCState.Playing or VLCState.Paused or VLCState.Buffering
                ? Math.Max(0, _mediaPlayer.Time)
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

            _mediaPlayer.Stop();
            _media?.Dispose();
            _media = null;

            // The discarded source's tracks must not leak into the new menu.
            _audioTracks = new List<TrackDescriptionTile>();
            _selectedAudioTrackId = -1;
            _audioListDirty = true;
            UpdateAudioIndicator();

            var media = new Media(_libVlc, new Uri(source.Url));
            media.AddOption(":no-video-title-show");
            media.AddOption(":no-osd");
            media.AddOption(":network-caching=2500");

            if (resume > 0)
            {
                media.AddOption($":start-time={resume / 1000.0:0.###}");
            }

            ApplyRequestHeaders(media, source.Headers);
            _media = media;
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
            if (!_mediaPlayer.Play(media))
            {
                throw new InvalidOperationException("VLC couldn't start this source.");
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

    private static void ApplyRequestHeaders(Media media, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return;
        }

        if (headers.TryGetValue("User-Agent", out var userAgent) && !string.IsNullOrWhiteSpace(userAgent))
        {
            media.AddOption($":http-user-agent={userAgent}");
        }

        if (headers.TryGetValue("Referer", out var referer) && !string.IsNullOrWhiteSpace(referer))
        {
            media.AddOption($":http-referrer={referer}");
        }

        if (headers.TryGetValue("Cookie", out var cookie) && !string.IsNullOrWhiteSpace(cookie))
        {
            media.AddOption($":http-cookie={cookie}");
        }
    }

    // --------------------------------------------------------------- VLC events

    // ------------------------------------------------------------ audio tracks

    private void MediaPlayer_ESAdded(object? sender, MediaPlayerESAddedEventArgs e) =>
        HandleEsChanged(e.Type);

    private void MediaPlayer_ESDeleted(object? sender, MediaPlayerESDeletedEventArgs e) =>
        HandleEsChanged(e.Type);

    private void MediaPlayer_ESSelected(object? sender, MediaPlayerESSelectedEventArgs e) =>
        HandleEsChanged(e.Type);

    private void HandleEsChanged(TrackType type)
    {
        if (type is not TrackType.Audio || _switching)
        {
            return;
        }

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
            _audioTracks = _mediaPlayer?.AudioTrackDescription
                               .Where(t => t is { Name.Length: > 0 })
                               .Select(t => new TrackDescriptionTile(t.Id, NameAudioTrack(t.Id, t.Name)))
                               .ToList()
                           ?? new List<TrackDescriptionTile>();
            _selectedAudioTrackId = _mediaPlayer?.AudioTrack ?? -1;
        }
        catch
        {
            _audioTracks = new List<TrackDescriptionTile>();
            _selectedAudioTrackId = -1;
        }

        _audioListDirty = false;
        UpdateAudioIndicator();
    }

    /// <summary>
    /// VLC names look like "Track 1 - [eng]" or "Track 1 - [au]" depending on
    /// the container; language tags become their display name.
    /// </summary>
    private static string NameAudioTrack(int id, string raw)
    {
        if (id == -1)
        {
            return "Off";
        }

        var cleaned = raw;
        var bracketStart = cleaned.IndexOf('[', StringComparison.Ordinal);
        var bracketEnd = cleaned.IndexOf(']', StringComparison.Ordinal);
        if (bracketStart >= 0 && bracketEnd > bracketStart)
        {
            var tag = cleaned[(bracketStart + 1)..bracketEnd].Trim();
            cleaned = cleaned[..bracketStart].Trim().TrimEnd('-', ' ');
            if (!string.IsNullOrWhiteSpace(tag) &&
                !tag.StartsWith("au", StringComparison.OrdinalIgnoreCase) &&
                tag.Length <= 8)
            {
                var language = SubtitleLanguages.DisplayName(tag);
                cleaned = string.IsNullOrWhiteSpace(language)
                    ? string.IsNullOrWhiteSpace(cleaned) ? tag : $"{cleaned} - {tag}"
                    : string.IsNullOrWhiteSpace(cleaned) ? language : $"{cleaned} - {language}";
            }
        }

        return string.IsNullOrWhiteSpace(cleaned) ? raw : cleaned;
    }

    private void AudioMenu_Opened(object? sender, object e)
    {
        if (_audioListDirty || _mediaPlayer?.State != VLCState.Playing || AudioMenu.Items.Count == 0)
        {
            RefreshAudioTracks();
        }

        RebuildAudioMenu();
    }

    private void RebuildAudioMenu()
    {
        AudioMenu.Items.Clear();

        if (_mediaPlayer is null || _audioTracks.Count == 0)
        {
            AudioMenu.Items.Add(new MenuFlyoutItem { Text = "No audio tracks", IsEnabled = false });
            return;
        }

        foreach (var track in _audioTracks)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = track.Name,
                IsChecked = track.Id == _selectedAudioTrackId,
                Tag = track.Id,
            };
            item.Click += AudioTrack_Click;
            AudioMenu.Items.Add(item);
        }
    }

    private void AudioTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: int id } || _mediaPlayer is null)
        {
            return;
        }

        if (!_mediaPlayer.SetAudioTrack(id))
        {
            return;
        }

        _selectedAudioTrackId = id;
        foreach (var item in AudioMenu.Items.OfType<ToggleMenuFlyoutItem>())
        {
            item.IsChecked = item.Tag is int other && other == id;
        }
    }

    private void UpdateAudioIndicator()
    {
        AudioMultiDot.Visibility = _audioTracks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record TrackDescriptionTile(int Id, string Name);

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

    private void MediaPlayer_Playing(object? sender, EventArgs e) => Dispatch(HandlePlaying);

    private void MediaPlayer_Paused(object? sender, EventArgs e) => Dispatch(HandlePaused);

    private void MediaPlayer_Stopped(object? sender, EventArgs e) => Dispatch(HandleStopped);

    private void MediaPlayer_EndReached(object? sender, EventArgs e) => Dispatch(HandleEndReached);

    private void MediaPlayer_EncounteredError(object? sender, EventArgs e) =>
        Dispatch(HandleEncounteredError);

    private void HandleEncounteredError()
    {
        // A remembered source that fails before playing is replaced silently.
        if (_cachedAttempt is not null)
        {
            _ = HandleCachedSourceFailureAsync();
            return;
        }

        ShowPlaybackError(
            "The selected source stopped responding or uses a format VLC can't open.",
            ViewModel.HasSources);
    }

    private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e) =>
        Dispatch(() => HandleTimeChanged(e.Time));

    private void MediaPlayer_LengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e) =>
        Dispatch(() => HandleLengthChanged(e.Length));

    private void MediaPlayer_Buffering(object? sender, MediaPlayerBufferingEventArgs e) =>
        Dispatch(() => HandleBuffering(e.Cache));

    private void HandlePlaying()
    {
        _switching = false;
        _playing = true;
        _ended = false;
        _errorVisible = false;

        LoadingOverlay.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        EndedText.Visibility = Visibility.Collapsed;
        HideBuffer();
        UpdatePlayPauseUi();
        SeekSlider.IsEnabled = _mediaPlayer?.IsSeekable == true;
        ShowChrome();
        StartChromeTimer();

        if (ResumePanel.Visibility == Visibility.Visible)
        {
            _resumeHintTimer?.Stop();
            _resumeHintTimer?.Start();
        }

        if (_resumeMs > 0 && _mediaPlayer is { IsSeekable: true })
        {
            _mediaPlayer.Time = _resumeMs;
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
    }

    private void HandlePaused()
    {
        _playing = false;
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
        UpdateTimeText(time, _mediaPlayer?.Length ?? 0);
    }

    private void HandleLengthChanged(long length)
    {
        _lengthMs = Math.Max(0, length);
        UpdateUpNextVisibility();
        SeekSlider.Maximum = Math.Max(1, length);
        SeekSlider.IsEnabled = length > 0 && _mediaPlayer?.IsSeekable == true;
        UpdateTimeText(_mediaPlayer?.Time ?? 0, length);
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
            ShowBuffer($"Buffering… {cache:0}%");
            CenterPlayButton.Visibility = Visibility.Collapsed;
        }
        else if (cache >= 100)
        {
            HideBuffer();
            UpdatePlayPauseUi();
        }
    }

    // ---------------------------------------------------------------- UI actions

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void InputLayer_Tapped(object sender, TappedRoutedEventArgs e)
    {
        VideoSurface.Focus(FocusState.Pointer);

        if (_sourcePanelOpen)
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
        if (_mediaPlayer is null)
        {
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

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
        }
        else
        {
            _mediaPlayer.Play();
        }

        ShowChrome();
        StartChromeTimer();
    }

    private void Back10_Click(object sender, RoutedEventArgs e) => SeekRelative(-10_000);

    private void Forward10_Click(object sender, RoutedEventArgs e) => SeekRelative(10_000);

    private void SeekRelative(long deltaMs)
    {
        if (_mediaPlayer is null || _mediaPlayer.Length <= 0 || !_mediaPlayer.IsSeekable)
        {
            return;
        }

        _mediaPlayer.Time = Math.Clamp(_mediaPlayer.Time + deltaMs, 0, _mediaPlayer.Length);
        ShowChrome();
        StartChromeTimer();
    }

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSeek)
        {
            return;
        }

        UpdateTimeText((long)e.NewValue, _mediaPlayer?.Length ?? 0);

        if (!_draggingSeek && _mediaPlayer is { IsSeekable: true })
        {
            _mediaPlayer.Time = (long)e.NewValue;
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
        if (_mediaPlayer is { IsSeekable: true })
        {
            _mediaPlayer.Time = (long)SeekSlider.Value;
        }

        StartChromeTimer();
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVolume || _mediaPlayer is null)
        {
            return;
        }

        var volume = (int)Math.Round(e.NewValue);
        _mediaPlayer.Volume = volume;

        if (volume > 0)
        {
            _lastVolume = volume;
            if (_mediaPlayer.Mute)
            {
                _mediaPlayer.Mute = false;
            }
        }

        UpdateVolumeUi();
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        if (_mediaPlayer.Mute || _mediaPlayer.Volume == 0)
        {
            _mediaPlayer.Mute = false;
            if (_mediaPlayer.Volume == 0)
            {
                _mediaPlayer.Volume = _lastVolume is > 0 and <= 100 ? _lastVolume : 80;
            }

            _suppressVolume = true;
            VolumeSlider.Value = _mediaPlayer.Volume;
            _suppressVolume = false;
        }
        else
        {
            _lastVolume = _mediaPlayer.Volume;
            _mediaPlayer.Mute = true;
        }

        UpdateVolumeUi();
        ShowChrome();
        StartChromeTimer();
    }

    private void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag } ||
            !float.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ||
            _mediaPlayer is null)
        {
            return;
        }

        _mediaPlayer.SetRate(rate);
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

    private void ShowChrome()
    {
        if (_disposed)
        {
            return;
        }

        _chromeHidden = false;
        Chrome.Visibility = Visibility.Visible;
        Chrome.Opacity = 1;
        Chrome.IsHitTestVisible = true;
    }

    private void HideChrome()
    {
        if (!_playing || _sourcePanelOpen || _subtitlesFlyoutOpen || _errorVisible || _ended || _draggingSeek)
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
    }

    private void StartChromeTimer()
    {
        if (_chromeHidden || !_playing || _sourcePanelOpen || _subtitlesFlyoutOpen || _errorVisible)
        {
            return;
        }

        _chromeTimer.Stop();
        _chromeTimer.Start();
    }

    private void InputLayer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _keyboardActive = false;
        if (_sourcePanelOpen || _errorVisible)
        {
            return;
        }

        ShowChrome();
        StartChromeTimer();
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _keyboardActive = false;
        if (_sourcePanelOpen || _errorVisible)
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
        if (_sourcePanelOpen || _subtitlesFlyoutOpen || _errorVisible)
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
        if (_sourcePanelOpen || _subtitlesFlyoutOpen)
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

    private void OpenSources_Click(object sender, RoutedEventArgs e) => OpenSources();

    private void OpenSources()
    {
        if (ViewModel.Sources.Count == 0)
        {
            return;
        }

        _sourcePanelOpen = true;
        SourceScrim.Visibility = Visibility.Visible;
        SourcePanel.Visibility = Visibility.Visible;
        _chromeTimer.Stop();
        ShowChrome();
        SourceList.Focus(FocusState.Programmatic);
    }

    private void CloseSources_Click(object sender, RoutedEventArgs e) => CloseSources();

    private void SourceScrim_Tapped(object sender, TappedRoutedEventArgs e) => CloseSources();

    private void CloseSources()
    {
        if (!_sourcePanelOpen)
        {
            return;
        }

        _sourcePanelOpen = false;
        SourceScrim.Visibility = Visibility.Collapsed;
        SourcePanel.Visibility = Visibility.Collapsed;
        ShowChrome();
        if (_playing)
        {
            StartChromeTimer();
        }
    }

    private void SourceList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SourceListItem { IsHeader: false, Source: { } source })
        {
            return;
        }

        CloseSources();

        if (_currentSource?.Url.Equals(source.Url, StringComparison.OrdinalIgnoreCase) == true &&
            !_errorVisible)
        {
            return;
        }

        PlaySource(source);
    }

    private void SubtitlesFlyout_Opened(object? sender, object e)
    {
        _subtitlesFlyoutOpen = true;
        _chromeTimer.Stop();
        ShowChrome();

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
        SubtitleActiveDot.Visibility = version is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateSubtitleCurrent();
        RefreshSubtitleFlyout();
    }

    /// <summary>
    /// Downloads and attaches a subtitle track. Auto-selection passes
    /// <paramref name="applyNow"/> false so a track chosen before playback
    /// starts is attached by the Playing handler instead of failing against an
    /// empty player.
    /// </summary>
    private async Task<bool> ApplySelectedSubtitleAsync(
        SubtitleVersionChoice? version,
        bool applyNow = true)
    {
        _selectedSubtitle = version;
        SubtitleActiveDot.Visibility = version is null ? Visibility.Collapsed : Visibility.Visible;
        ViewModel.SelectSubtitle(version);

        if (version is null)
        {
            _selectedSubtitlePath = null;
            _mediaPlayer?.SetSpu(-1);
            _subtitleApplied = true;
            ViewModel.SubtitleStatus = "";
            UpdateSubtitleCurrent();
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
        ViewModel.SubtitleStatus = "";
        UpdateSubtitleCurrent();
        RememberSubtitleLanguage(version);

        if (!applyNow && _currentSource is null)
        {
            // The Playing handler attaches the downloaded track once a source starts.
            return true;
        }

        return ApplyLocalSubtitle(path);
    }

    /// <summary>
    /// Keeps the language the user actually watches in as the default for the
    /// next title or episode. Choosing Off is deliberately not remembered.
    /// </summary>
    private static void RememberSubtitleLanguage(SubtitleVersionChoice version)
    {
        var code = SubtitleLanguages.CanonicalCode(version.LanguageCode);
        if (string.IsNullOrWhiteSpace(code) ||
            code.Equals(App.Settings.PreferredSubtitleLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        App.Settings.PreferredSubtitleLanguage = code;
        _ = App.Settings.SaveAsync();
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
        if (_mediaPlayer is null)
        {
            return true;
        }

        try
        {
            if (!_mediaPlayer.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, true))
            {
                ViewModel.SubtitleStatus = "Couldn't load that subtitle.";
                return false;
            }

            _subtitleApplied = true;
            return true;
        }
        catch (Exception exception)
        {
            ViewModel.SubtitleStatus = $"Couldn't load that subtitle. {exception.Message}";
            return false;
        }
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
            LoadingOverlay.Visibility = Visibility.Visible;
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
        else if (_sourcePanelOpen)
        {
            CloseSources();
            args.Handled = true;
        }
        else if (_subtitlesFlyoutOpen)
        {
            SubtitlesFlyout.Hide();
            args.Handled = true;
        }
    }

    // ---------------------------------------------------------------- small helpers

    private void ShowBuffer(string text)
    {
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

        CenterPlayButton.Visibility = _playing || _errorVisible || _sourcePanelOpen || _disposed
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateVolumeUi()
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        var muted = _mediaPlayer.Mute || _mediaPlayer.Volume == 0;
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
        HideBuffer();
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
        _cachedWatchdog?.Stop();
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

        UnhookMediaPlayer();

        try
        {
            VideoSurface.MediaPlayer = null;
        }
        catch
        {
            // The swap chain may already be gone during shutdown.
        }

        try
        {
            _mediaPlayer?.Stop();
        }
        catch
        {
        }

        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
        _media?.Dispose();
        _media = null;
        _libVlc?.Dispose();
        _libVlc = null;
    }
}

using System.Globalization;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.Models;
using Pirsa.Services;
using Pirsa.ViewModels;
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

    private readonly PlayerViewModel _viewModel = new(App.Addons);
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
    private bool _chromeHidden = true;
    private bool _keyboardActive;
    private bool _isFullScreen;
    private long _resumeMs;
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

        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SeekSlider_PointerPressed), true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SeekSlider_PointerReleased), true);
        SeekSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(SeekSlider_PointerCaptureLost), true);
        Root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Root_PointerMoved), true);
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Root_PointerPressed), true);
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Root_KeyDown), true);

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
        _ = LoadSourcesAsync(request);
        _ = LoadSubtitlesAsync(request);
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
    }

    private async Task LoadSubtitlesAsync(PlaybackRequest request)
    {
        _subtitleCts?.Cancel();
        _subtitleCts?.Dispose();
        _subtitleCts = new CancellationTokenSource();

        try
        {
            await ViewModel.LoadSubtitlesAsync(request, _subtitleCts.Token);
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

    private async Task LoadSourcesAsync(PlaybackRequest request)
    {
        _sourceCts?.Cancel();
        _sourceCts?.Dispose();
        _sourceCts = new CancellationTokenSource();

        if (ViewModel.Request != request)
        {
            ViewModel.Request = request;
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
        ShowBuffer("Starting stream…");

        _pendingSource = ViewModel.SelectedSource;
        TryStartPlayback();
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

            _ended = false;
            _resumeMs = resume;
            _switching = true;

            _mediaPlayer.Stop();
            _media?.Dispose();
            _media = null;

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

    private void MediaPlayer_EncounteredError(object? sender, EventArgs e) => Dispatch(() =>
        ShowPlaybackError(
            "The selected source stopped responding or uses a format VLC can't open.",
            ViewModel.HasSources));

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

        if (_resumeMs > 0 && _mediaPlayer is { IsSeekable: true })
        {
            _mediaPlayer.Time = _resumeMs;
        }

        _resumeMs = 0;

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
        UpdatePlayPauseUi();
        ShowChrome();
        _chromeTimer.Stop();
    }

    private void HandleStopped()
    {
        if (_switching || _disposed)
        {
            return;
        }

        _playing = false;
        UpdatePlayPauseUi();
        ShowChrome();
    }

    private void HandleEndReached()
    {
        _playing = false;
        _ended = true;
        HideBuffer();
        EndedText.Visibility = Visibility.Visible;
        UpdatePlayPauseUi();
        ShowChrome();
        _chromeTimer.Stop();
    }

    private void HandleTimeChanged(long time)
    {
        if (_draggingSeek)
        {
            return;
        }

        _suppressSeek = true;
        SeekSlider.Value = Math.Clamp(time, 0, (long)SeekSlider.Maximum);
        _suppressSeek = false;
        UpdateTimeText(time, _mediaPlayer?.Length ?? 0);
    }

    private void HandleLengthChanged(long length)
    {
        SeekSlider.Maximum = Math.Max(1, length);
        SeekSlider.IsEnabled = length > 0 && _mediaPlayer?.IsSeekable == true;
        UpdateTimeText(_mediaPlayer?.Time ?? 0, length);
    }

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

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        _keyboardActive = true;
        ShowChrome();
        StartChromeTimer();
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
        if (e.ClickedItem is not PlaybackSource source)
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

    private async Task<bool> ApplySelectedSubtitleAsync(SubtitleVersionChoice? version)
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
        return ApplyLocalSubtitle(path);
    }

    private string EnsureSubtitleDirectory()
    {
        if (_subtitleDirectory is null)
        {
            _subtitleDirectory = Path.Combine(
                ApplicationData.Current.TemporaryFolder.Path,
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
        ToolTipService.SetToolTip(FullScreenButton, fullscreen ? "Exit full screen" : "Full screen");
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
        ToolTipService.SetToolTip(VolumeButton, muted ? "Unmute" : "Mute");
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

        _disposed = true;
        _chromeTimer.Stop();
        _sourceCts?.Cancel();
        _sourceCts?.Dispose();
        _sourceCts = null;
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

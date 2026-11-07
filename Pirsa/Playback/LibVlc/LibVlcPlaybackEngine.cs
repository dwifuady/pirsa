using System.Diagnostics;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Microsoft.UI.Xaml;
using Pirsa.Helpers;
using Pirsa.Services;

namespace Pirsa.Playback.LibVlc;

/// <summary>
/// libVLC backend built on LibVLCSharp's WinUI SwapChainPanel view. VLC handles
/// the container/codec variety these addons throw at it (MKV, HEVC, HDR, HLS,
/// DASH) while the page keeps its own WinUI chrome.
/// </summary>
public sealed class LibVlcPlaybackEngine : IPlaybackEngine
{
    private static readonly object CoreLock = new();
    private static bool _coreReady;

    /// <summary>
    /// LibVLC's demuxer cache for on-demand video. It is a jitter buffer, not a
    /// seek window: VLC's HTTP access answers a seek with a fresh range request,
    /// so nothing here is ever replayed from memory. What it does control is how
    /// long VLC waits for the buffer to refill after a start or a seek (the
    /// buffering percentage is measured against this target), which is why it is
    /// kept short. A real seek window needs the local read-ahead cache in front
    /// of VLC, not a bigger VLC buffer.
    /// </summary>
    private const int VodNetworkCachingMs = 3000;

    /// <summary>
    /// The same buffer for live TV, kept shorter still: every cached second is a
    /// second further behind the live edge.
    /// </summary>
    private const int LiveNetworkCachingMs = 2500;

    private readonly VideoView _surface = new()
    {
        IsTabStop = true,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _media;
    private bool _ready;
    private bool _disposed;
    private SubtitleStyle _subtitleStyle = new(1.0, SubtitleBackground.Dim);

    public LibVlcPlaybackEngine()
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_surface, "Video");
        _surface.Initialized += Surface_Initialized;

        // Reset here, while the page is being constructed, so the "open title"
        // line OnNavigatedTo writes next survives the engine starting up.
        PlayerLog.Reset();
    }

    public string Id => "libvlc";

    public FrameworkElement Surface => _surface;

    public bool IsReady => _ready && _mediaPlayer is not null;

    public PlaybackState State
    {
        get
        {
            try
            {
                return _mediaPlayer?.State switch
                {
                    VLCState.Opening => PlaybackState.Opening,
                    VLCState.Buffering => PlaybackState.Buffering,
                    VLCState.Playing => PlaybackState.Playing,
                    VLCState.Paused => PlaybackState.Paused,
                    VLCState.Stopped => PlaybackState.Stopped,
                    VLCState.Ended => PlaybackState.Ended,
                    VLCState.Error => PlaybackState.Error,
                    _ => PlaybackState.Idle,
                };
            }
            catch
            {
                return PlaybackState.Idle;
            }
        }
    }

    public bool IsPlaying => State == PlaybackState.Playing;

    public bool CutsAudioOnPause => true;

    public bool SubtitlesArePictureRelative => true;

    public bool IsSeekable
    {
        get
        {
            try
            {
                return _mediaPlayer?.IsSeekable == true;
            }
            catch
            {
                return false;
            }
        }
    }

    public int Volume
    {
        get
        {
            try
            {
                return _mediaPlayer?.Volume ?? 0;
            }
            catch
            {
                return 0;
            }
        }
        set
        {
            try
            {
                if (_mediaPlayer is not null)
                {
                    _mediaPlayer.Volume = Math.Clamp(value, 0, 100);
                }
            }
            catch
            {
                // Volume is best effort.
            }
        }
    }

    public bool IsMuted
    {
        get
        {
            try
            {
                return _mediaPlayer?.Mute == true;
            }
            catch
            {
                return false;
            }
        }
    }

    public long PositionMs
    {
        get
        {
            try
            {
                return _mediaPlayer?.Time ?? 0;
            }
            catch
            {
                return 0;
            }
        }
        set
        {
            try
            {
                if (_mediaPlayer is { IsSeekable: true })
                {
                    _mediaPlayer.Time = Math.Max(0, value);
                }
            }
            catch
            {
                // Seeking is best effort; an unseekable stream ignores it.
            }
        }
    }

    public long DurationMs
    {
        get
        {
            try
            {
                return _mediaPlayer?.Length ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    public IReadOnlyList<PlaybackChapter> Chapters
    {
        get
        {
            try
            {
                if (_mediaPlayer is not { } player || player.ChapterCount <= 0)
                {
                    return [];
                }

                var descriptions = player.FullChapterDescriptions(-1);
                if (descriptions is null)
                {
                    return [];
                }

                // LibVLC reports chapter times in microseconds.
                return descriptions
                    .Where(chapter => chapter.TimeOffset >= 0)
                    .Select(chapter => new PlaybackChapter(
                        chapter.TimeOffset / 1000,
                        string.IsNullOrWhiteSpace(chapter.Name) ? null : chapter.Name))
                    .OrderBy(chapter => chapter.StartMs)
                    .ToList();
            }
            catch
            {
                return [];
            }
        }
    }

    public IReadOnlyList<PlaybackTrack> AudioTracks
    {
        get
        {
            try
            {
                return _mediaPlayer?.AudioTrackDescription
                           .Where(track => track is { Name.Length: > 0 })
                           .Select(track => new PlaybackTrack(
                               track.Id,
                               NameAudioTrack(track.Id, track.Name),
                               AudioLanguageCode(track.Name)))
                           .ToList()
                       ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    public IReadOnlyList<PlaybackTrack> SubtitleTracks
    {
        get
        {
            try
            {
                return _mediaPlayer?.SpuDescription
                           .Where(track => track.Id >= 0 && !string.IsNullOrWhiteSpace(track.Name))
                           .Select(track => DescribeSubtitleTrack(track.Id, track.Name))
                           .ToList()
                       ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    public long CurrentAudioTrackId
    {
        get
        {
            try
            {
                return _mediaPlayer?.AudioTrack ?? -1;
            }
            catch
            {
                return -1;
            }
        }
    }

    public event EventHandler? Ready;

    public event EventHandler? Playing;

    public event EventHandler? Paused;

    public event EventHandler? Stopped;

    public event EventHandler? EndReached;

    public event EventHandler<PlaybackErrorEventArgs>? Failed;

    public event EventHandler<PlaybackPositionChangedEventArgs>? PositionChanged;

    public event EventHandler<PlaybackDurationChangedEventArgs>? DurationChanged;

    public event EventHandler<PlaybackBufferingEventArgs>? BufferingChanged;

    public event EventHandler<PlaybackTracksChangedEventArgs>? TracksChanged;

    public bool TryPlay(PlaybackMedia media)
    {
        if (_mediaPlayer is null || _libVlc is null)
        {
            return false;
        }

        Stop();

        var vlcMedia = new Media(_libVlc, new Uri(media.Url));
        vlcMedia.AddOption(":no-video-title-show");
        vlcMedia.AddOption(":no-osd");
        vlcMedia.AddOption($":network-caching={NetworkCachingFor(media)}");

        // VLC's freetype text renderer reads these at media start. Embedded ASS
        // tracks can still carry their own styling, which is expected.
        vlcMedia.AddOption($":sub-text-scale={SubtitleTextScalePercent()}");
        vlcMedia.AddOption(
            $":freetype-background-opacity={SubtitleBackgroundOpacity()}");

        PlayerLog.Write(
            $"vlc subtitle style scale={SubtitleTextScalePercent()}% " +
            $"background={_subtitleStyle.Background} " +
            $"opacity={SubtitleBackgroundOpacity()}");

        if (media.StartPositionMs > 0)
        {
            vlcMedia.AddOption($":start-time={media.StartPositionMs / 1000.0:0.###}");
        }

        if (media.Headers is not null)
        {
            ApplyRequestHeaders(vlcMedia, media.Headers);
        }

        _media = vlcMedia;
        return _mediaPlayer.Play(vlcMedia);
    }

    public void SetPaused(bool paused)
    {
        try
        {
            _mediaPlayer?.SetPause(paused);
        }
        catch
        {
            // Pausing or resuming a stopped player is harmless.
        }
    }

    public void Stop()
    {
        try
        {
            _mediaPlayer?.Stop();
        }
        catch
        {
        }

        _media?.Dispose();
        _media = null;
    }

    public void SetRate(double rate)
    {
        try
        {
            _mediaPlayer?.SetRate((float)rate);
        }
        catch
        {
            // Rate changes are best effort.
        }
    }

    public void SetMuted(bool muted)
    {
        try
        {
            if (_mediaPlayer is not null)
            {
                _mediaPlayer.Mute = muted;
            }
        }
        catch
        {
        }
    }

    public bool SelectAudioTrack(long id)
    {
        if (_mediaPlayer is null || id is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        try
        {
            return _mediaPlayer.SetAudioTrack((int)id);
        }
        catch
        {
            return false;
        }
    }

    public bool SelectSubtitleTrack(long id)
    {
        if (_mediaPlayer is null || id is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        try
        {
            return _mediaPlayer.SetSpu((int)id);
        }
        catch
        {
            return false;
        }
    }

    public bool AddSubtitleFile(string path)
    {
        if (_mediaPlayer is null || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return _mediaPlayer.AddSlave(
                MediaSlaveType.Subtitle,
                new Uri(path).AbsoluteUri,
                select: true);
        }
        catch
        {
            return false;
        }
    }

    public void SetSubtitleDelay(long delayMs)
    {
        try
        {
            // libvlc measures the delay in microseconds and resets it for every
            // new media, so the page reapplies it on each source start.
            _mediaPlayer?.SetSpuDelay(delayMs * 1000);
        }
        catch
        {
            // Timing is best effort; playback continues either way.
        }
    }

    public void SetSubtitleLift(int devicePixels) =>
        SubtitleLift.Set(_mediaPlayer, devicePixels);

    public void SetSubtitleStyle(SubtitleStyle style)
    {
        _subtitleStyle = style;

        // Media options cover the next input; the vout variables also update
        // the live text renderer when the player is already running.
        SubtitleLift.SetStyle(
            _mediaPlayer,
            SubtitleTextScalePercent(),
            SubtitleBackgroundOpacity());
    }

    /// <summary>VLC's sub-text-scale is an integer percentage; 100 is default.</summary>
    private int SubtitleTextScalePercent() =>
        Math.Clamp((int)Math.Round(_subtitleStyle.TextScale * 100), 50, 200);

    private int SubtitleBackgroundOpacity() => _subtitleStyle.Background switch
    {
        SubtitleBackground.None => 0,
        SubtitleBackground.Solid => 255,
        _ => 128,
    };

    public bool TryGetVideoSize(out uint width, out uint height)
    {
        width = 0;
        height = 0;

        try
        {
            return _mediaPlayer is not null &&
                   _mediaPlayer.Size(0, ref width, ref height) &&
                   width > 0 &&
                   height > 0;
        }
        catch
        {
            return false;
        }
    }

    public void FocusSurface(FocusState state = FocusState.Programmatic) =>
        _surface.Focus(state);

    /// <summary>
    /// Detaches the surface now and tears the native engine down on a worker
    /// thread, so leaving the player does not freeze on VLC's stop.
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _surface.Initialized -= Surface_Initialized;
        UnhookMediaPlayer();

        try
        {
            _surface.MediaPlayer = null;
        }
        catch
        {
            // The swap chain may already be gone during shutdown.
        }

        var player = _mediaPlayer;
        var media = _media;
        var vlc = _libVlc;
        _mediaPlayer = null;
        _media = null;
        _libVlc = null;
        _ready = false;

        await Task.Run(() =>
        {
            var elapsed = Stopwatch.StartNew();

            try
            {
                player?.Stop();
            }
            catch
            {
            }

            var stopMs = elapsed.ElapsedMilliseconds;

            try
            {
                player?.Dispose();
            }
            catch
            {
            }

            try
            {
                media?.Dispose();
            }
            catch
            {
            }

            try
            {
                vlc?.Dispose();
            }
            catch
            {
            }

            PlayerLog.Write($"video engine released in {elapsed.ElapsedMilliseconds} ms (stop {stopMs} ms)");
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _surface.Initialized -= Surface_Initialized;
        UnhookMediaPlayer();

        try
        {
            _surface.MediaPlayer = null;
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
        _ready = false;
    }

    // ---------------------------------------------------------------- engine setup

    private async void Surface_Initialized(object? sender, EventArgs e)
    {
        if (_disposed || _ready)
        {
            return;
        }

        try
        {
            // The surface can initialize while the page is still in its
            // constructor. Yield once so the page can subscribe to Ready and
            // Failed before the engine starts or reports an error.
            await Task.Yield();
            await LibVlcRuntime.WarmUpAsync();
            if (_disposed || _ready)
            {
                return;
            }

            EnsureCoreInitialized();

            var options = new List<string>(_surface.SwapChainOptions)
            {
                "--no-video-title-show",
                "--no-osd",
                "--aout=mmdevice,directsound",
                "--verbose=2",
            };

            PlayerLog.Write($"libvlc options: {string.Join(' ', options)}");

            _libVlc = new LibVLC([.. options]);
            _libVlc.Log += (_, log) =>
                PlayerLog.WriteEngine("vlc", log.Level.ToString(), log.Module, log.Message);
            _mediaPlayer = new MediaPlayer(_libVlc)
            {
                Volume = 80,
            };

            HookMediaPlayer();
            _surface.MediaPlayer = _mediaPlayer;
            _ready = true;

            Ready?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            Failed?.Invoke(
                this,
                new PlaybackErrorEventArgs(
                    $"The video engine couldn't start. {exception.Message}",
                    isFatal: true));
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

    // ---------------------------------------------------------------- media options

    private static int NetworkCachingFor(PlaybackMedia media) =>
        media.IsLive ? LiveNetworkCachingMs : VodNetworkCachingMs;

    private static void ApplyRequestHeaders(
        Media media,
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return;
        }

        if (headers.TryGetValue("User-Agent", out var userAgent) &&
            !string.IsNullOrWhiteSpace(userAgent))
        {
            media.AddOption($":http-user-agent={userAgent}");
        }

        if (headers.TryGetValue("Referer", out var referer) &&
            !string.IsNullOrWhiteSpace(referer))
        {
            media.AddOption($":http-referrer={referer}");
        }

        if (headers.TryGetValue("Cookie", out var cookie) &&
            !string.IsNullOrWhiteSpace(cookie))
        {
            media.AddOption($":http-cookie={cookie}");
        }
    }

    // ---------------------------------------------------------------- VLC events

    private void MediaPlayer_Playing(object? sender, EventArgs e) =>
        Playing?.Invoke(this, EventArgs.Empty);

    private void MediaPlayer_Paused(object? sender, EventArgs e) =>
        Paused?.Invoke(this, EventArgs.Empty);

    private void MediaPlayer_Stopped(object? sender, EventArgs e) =>
        Stopped?.Invoke(this, EventArgs.Empty);

    private void MediaPlayer_EndReached(object? sender, EventArgs e) =>
        EndReached?.Invoke(this, EventArgs.Empty);

    private void MediaPlayer_EncounteredError(object? sender, EventArgs e) =>
        Failed?.Invoke(
            this,
            new PlaybackErrorEventArgs(
                "The selected source stopped responding or uses a format this video engine can't open."));

    private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e) =>
        PositionChanged?.Invoke(this, new PlaybackPositionChangedEventArgs(e.Time));

    private void MediaPlayer_LengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e) =>
        DurationChanged?.Invoke(this, new PlaybackDurationChangedEventArgs(e.Length));

    private void MediaPlayer_Buffering(object? sender, MediaPlayerBufferingEventArgs e) =>
        BufferingChanged?.Invoke(this, new PlaybackBufferingEventArgs(e.Cache));

    private void MediaPlayer_ESAdded(object? sender, MediaPlayerESAddedEventArgs e) =>
        RaiseTracksChanged(e.Type);

    private void MediaPlayer_ESDeleted(object? sender, MediaPlayerESDeletedEventArgs e) =>
        RaiseTracksChanged(e.Type);

    private void MediaPlayer_ESSelected(object? sender, MediaPlayerESSelectedEventArgs e) =>
        RaiseTracksChanged(e.Type);

    private void RaiseTracksChanged(TrackType type)
    {
        var kind = type switch
        {
            TrackType.Audio => PlaybackTrackKind.Audio,
            TrackType.Text => PlaybackTrackKind.Subtitle,
            TrackType.Video => PlaybackTrackKind.Video,
            _ => (PlaybackTrackKind?)null,
        };

        if (kind is { } value)
        {
            TracksChanged?.Invoke(this, new PlaybackTracksChangedEventArgs(value));
        }
    }

    // ---------------------------------------------------------------- track names

    /// <summary>
    /// VLC names look like "Track 1 - [eng]" or "Track 1 - [au]" depending on
    /// the container; language tags become their display name.
    /// </summary>
    private static string NameAudioTrack(long id, string raw)
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

    /// <summary>
    /// VLC names audio tracks like "Track 1 - [eng]". The tag is the only
    /// reliable language signal, so expose it as the canonical code used by the
    /// preferred-audio setting.
    /// </summary>
    private static string? AudioLanguageCode(string raw)
    {
        var bracketStart = raw.IndexOf('[', StringComparison.Ordinal);
        var bracketEnd = raw.IndexOf(']', StringComparison.Ordinal);
        if (bracketStart < 0 || bracketEnd <= bracketStart)
        {
            return null;
        }

        var tag = raw[(bracketStart + 1)..bracketEnd].Trim();
        if (tag.Length is 0 or > 8 ||
            tag.StartsWith("au", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("und", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return SubtitleLanguages.CanonicalCode(tag);
    }

    /// <summary>
    /// VLC names tracks like "Track 1 - [eng]"; the bracketed tag is the only
    /// reliable language signal in the container.
    /// </summary>
    private static PlaybackTrack DescribeSubtitleTrack(long id, string raw)
    {
        var cleaned = raw.Trim();
        var bracketStart = cleaned.IndexOf('[', StringComparison.Ordinal);
        var bracketEnd = cleaned.IndexOf(']', StringComparison.Ordinal);

        if (bracketStart >= 0 && bracketEnd > bracketStart)
        {
            var tag = cleaned[(bracketStart + 1)..bracketEnd].Trim();
            cleaned = cleaned[..bracketStart].Trim().TrimEnd('-', ' ');

            if (!string.IsNullOrWhiteSpace(tag) &&
                tag.Length <= 8 &&
                !tag.Equals("und", StringComparison.OrdinalIgnoreCase) &&
                !tag.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                var language = SubtitleLanguages.DisplayName(tag);
                if (!string.IsNullOrWhiteSpace(language))
                {
                    var name = string.IsNullOrWhiteSpace(cleaned)
                        ? language
                        : $"{language} · {cleaned}";
                    return new PlaybackTrack(id, name, tag, cleaned);
                }
            }
        }

        return new PlaybackTrack(
            id,
            string.IsNullOrWhiteSpace(cleaned) ? raw : cleaned,
            LanguageCode: null,
            Title: cleaned);
    }
}

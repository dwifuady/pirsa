using Microsoft.UI.Xaml;

namespace Pirsa.Playback;

/// <summary>
/// The player page's view of a video backend. It owns one active media, the
/// transport, track selection and the video surface; source discovery, resume,
/// history and subtitles stay in the page and view model.
/// </summary>
public interface IPlaybackEngine : IDisposable
{
    /// <summary>Stable backend id, for example "libvlc" or "mpv".</summary>
    string Id { get; }

    /// <summary>
    /// The control that shows video. The page adds it to its video host and
    /// keeps the transport chrome above it.
    /// </summary>
    FrameworkElement Surface { get; }

    /// <summary>True once the native engine and player are ready for playback.</summary>
    bool IsReady { get; }

    PlaybackState State { get; }

    bool IsPlaying { get; }

    /// <summary>
    /// True when pausing leaves audio already queued in the device, so the page
    /// should mute the Windows audio session to cut it immediately.
    /// </summary>
    bool CutsAudioOnPause { get; }

    bool IsSeekable { get; }

    /// <summary>
    /// True when the backend positions subtitles from the bottom of the picture,
    /// so letterbox bars already keep them above the window bottom. False when
    /// it positions them from the bottom of the video surface.
    /// </summary>
    bool SubtitlesArePictureRelative { get; }

    /// <summary>0–100, matching the transport slider.</summary>
    int Volume { get; set; }

    bool IsMuted { get; }

    /// <summary>Playhead position. Assigning seeks, like the old MediaPlayer.Time.</summary>
    long PositionMs { get; set; }

    long DurationMs { get; }

    IReadOnlyList<PlaybackTrack> AudioTracks { get; }

    IReadOnlyList<PlaybackTrack> SubtitleTracks { get; }

    long CurrentAudioTrackId { get; }

    /// <summary>Raised after the native engine and player exist.</summary>
    event EventHandler? Ready;

    event EventHandler? Playing;

    event EventHandler? Paused;

    event EventHandler? Stopped;

    event EventHandler? EndReached;

    /// <summary>
    /// A source failed, or the engine itself could not start. Raised on any
    /// thread; the page marshals to the UI thread.
    /// </summary>
    event EventHandler<PlaybackErrorEventArgs>? Failed;

    event EventHandler<PlaybackPositionChangedEventArgs>? PositionChanged;

    event EventHandler<PlaybackDurationChangedEventArgs>? DurationChanged;

    event EventHandler<PlaybackBufferingEventArgs>? BufferingChanged;

    /// <summary>
    /// Audio or subtitle tracks were added, removed or selected. Raised on any
    /// thread; the page marshals to the UI thread.
    /// </summary>
    event EventHandler<PlaybackTracksChangedEventArgs>? TracksChanged;

    /// <summary>
    /// Starts a source. Returns false when the backend could not queue it at
    /// all; later failures arrive through <see cref="Failed"/>.
    /// </summary>
    bool TryPlay(PlaybackMedia media);

    /// <summary>Explicit pause or resume; never a toggle.</summary>
    void SetPaused(bool paused);

    void Stop();

    /// <summary>
    /// Releases the native engine without blocking the UI thread. The surface
    /// is detached synchronously; the caller may navigate once the task
    /// completes (or after a bounded wait).
    /// </summary>
    Task ShutdownAsync();

    void SetRate(double rate);

    void SetMuted(bool muted);

    bool SelectAudioTrack(long id);

    /// <summary>Pass -1 to turn subtitles off.</summary>
    bool SelectSubtitleTrack(long id);

    /// <summary>Attaches a local subtitle file and selects it.</summary>
    bool AddSubtitleFile(string path);

    /// <summary>Sets the subtitle timing offset; positive shows subtitles later.</summary>
    void SetSubtitleDelay(long delayMs);

    /// <summary>
    /// Lifts the engine's own subtitles by a bottom margin in device pixels, so
    /// the player chrome does not cover them. Zero restores the default
    /// placement; backends that cannot move their subtitles may ignore it.
    /// </summary>
    void SetSubtitleLift(int devicePixels);

    /// <summary>
    /// Current video size in pixels, when the engine knows it. Used to exclude
    /// letterbox space from the subtitle lift.
    /// </summary>
    bool TryGetVideoSize(out uint width, out uint height);

    void FocusSurface(FocusState state = FocusState.Programmatic);
}

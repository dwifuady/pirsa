namespace Pirsa.Playback;

public enum PlaybackState
{
    Idle,
    Opening,
    Buffering,
    Playing,
    Paused,
    Stopped,
    Ended,
    Error,
}

public enum PlaybackTrackKind
{
    Audio,
    Subtitle,
    Video,
}

/// <summary>
/// One source handed to a backend. Headers are only meaningful when the page
/// is not already proxying through the local stream cache.
/// </summary>
public sealed record PlaybackMedia(
    string Url,
    long StartPositionMs = 0,
    bool IsLive = false,
    bool IsHls = false,
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// An engine-neutral audio or subtitle track. Ids belong to the current engine
/// session only; they are never persisted.
/// </summary>
public sealed record PlaybackTrack(
    long Id,
    string DisplayName,
    string? LanguageCode = null,
    string? Title = null,
    bool IsExternal = false);

/// <summary>Box behind rendered subtitles. Embedded ASS may carry its own.</summary>
public enum SubtitleBackground
{
    None,
    Dim,
    Solid,
}

/// <summary>
/// Engine-neutral subtitle appearance. TextScale 1.0 is the backend default;
/// background is a best-effort hint for embedded subtitle renderers.
/// </summary>
public sealed record SubtitleStyle(double TextScale, SubtitleBackground Background);

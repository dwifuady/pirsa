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
/// A chapter the playing file declares. Titles often name an opening, ending or
/// recap, which is how skip segments are found for releases the community
/// database does not know.
/// </summary>
public sealed record PlaybackChapter(long StartMs, string? Title);

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

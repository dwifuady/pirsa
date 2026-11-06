namespace Pirsa.Playback;

public sealed class PlaybackErrorEventArgs(string message, bool isFatal = false) : EventArgs
{
    public string Message { get; } = message;

    /// <summary>
    /// True when the backend itself could not start, so switching sources
    /// cannot help.
    /// </summary>
    public bool IsFatal { get; } = isFatal;
}

public sealed class PlaybackPositionChangedEventArgs(long positionMs) : EventArgs
{
    public long PositionMs { get; } = positionMs;
}

public sealed class PlaybackDurationChangedEventArgs(long durationMs) : EventArgs
{
    public long DurationMs { get; } = durationMs;
}

public sealed class PlaybackBufferingEventArgs(float cachePercent) : EventArgs
{
    public float CachePercent { get; } = cachePercent;
}

public sealed class PlaybackTracksChangedEventArgs(PlaybackTrackKind kind) : EventArgs
{
    public PlaybackTrackKind Kind { get; } = kind;
}

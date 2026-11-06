using System.Text.Json.Serialization;

namespace Pirsa.Services.Sync;

/// <summary>Whether a synced item is a movie or a series episode.</summary>
public enum SyncMediaKind
{
    Movie,
    Series,
}

/// <summary>Watched, or the timestamped removal of a watched mark.</summary>
public enum WatchEventKind
{
    Watched,
    Unwatched,
}

/// <summary>What a provider can do. Settings uses this to shape its copy.</summary>
[Flags]
public enum SyncCapabilities
{
    None = 0,
    Watched = 1,
    Watchlist = 2,
    Scrobble = 4,
    Ratings = 8,
}

public enum SyncAuthKind
{
    ApiKey,
    Credentials,
    UrlWithPassword,
    OAuthDevice,
}

/// <summary>
/// One watched or unwatched fact for a movie, or for a single episode of a
/// series. <see cref="At"/> is the moment the fact became true, and is the only
/// thing a merge uses to pick a side.
/// </summary>
public sealed record WatchEvent(
    string ImdbId,
    SyncMediaKind Kind,
    int? Season,
    int? Episode,
    WatchEventKind Change,
    DateTimeOffset At,
    string? Name = null,
    string? Poster = null,
    string? Type = null)
{
    [JsonIgnore]
    public string Key => SyncKey.For(ImdbId, Kind, Season, Episode);
}

/// <summary>One added or removed watchlist title.</summary>
public sealed record WatchlistChange(
    string ImdbId,
    SyncMediaKind Kind,
    bool Added,
    DateTimeOffset At,
    string? Name = null,
    string? Poster = null,
    string? Type = null)
{
    [JsonIgnore]
    public string Key => SyncKey.For(ImdbId, Kind);
}

/// <summary>The keys both sides match on. A provider must map to these.</summary>
public static class SyncKey
{
    public static string For(string imdbId, SyncMediaKind kind) =>
        $"{imdbId}|{kind}";

    public static string For(string imdbId, SyncMediaKind kind, int? season, int? episode) =>
        kind == SyncMediaKind.Series
            ? $"{imdbId}|{kind}|{season ?? 0}|{episode ?? 0}"
            : $"{imdbId}|{kind}";

    /// <summary>The IMDb id inside a Stremio id, or null for a custom prefix.</summary>
    public static string? ImdbFrom(string? id) =>
        id is { Length: > 2 } value && value.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
            ? value
            : null;
}

/// <summary>What a provider returned for this account.</summary>
public sealed record RemoteSnapshot(
    IReadOnlyList<WatchEvent> Watched,
    IReadOnlyList<WatchlistChange> Watchlist)
{
    public static RemoteSnapshot Empty { get; } = new([], []);
}

/// <summary>Everything this device knows, ready to push.</summary>
public sealed record SyncSnapshot(
    IReadOnlyList<WatchEvent> Watched,
    IReadOnlyList<WatchEvent> Unwatched,
    IReadOnlyList<WatchlistChange> Watchlist,
    IReadOnlyList<WatchlistChange> WatchlistRemoved);

/// <summary>
/// The remote facts that are newer than the local ones, plus the tombstones a
/// pull introduced. Applying this locally is safe to call on every sync.
/// </summary>
public sealed record SyncPlan(
    IReadOnlyList<WatchEvent> ApplyWatched,
    IReadOnlyList<WatchlistChange> ApplyWatchlist,
    IReadOnlyList<WatchEvent> AddUnwatched,
    IReadOnlyList<WatchlistChange> AddWatchlistRemoved);

/// <summary>One connected account, persisted in <c>sync.json</c>.</summary>
public sealed class SyncConnection
{
    public string ProviderId { get; set; } = "";

    /// <summary>Shown in Settings; never a secret.</summary>
    public string AccountLabel { get; set; } = "";

    public DateTimeOffset? LastSyncedAt { get; set; }

    public string? LastError { get; set; }
}

public sealed record SyncResult(bool Success, string Message)
{
    public static SyncResult Ok(string message) => new(true, message);

    public static SyncResult Fail(string message) => new(false, message);
}

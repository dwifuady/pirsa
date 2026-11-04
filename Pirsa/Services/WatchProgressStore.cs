using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Pirsa.Models;

namespace Pirsa.Services;

/// <summary>
/// One saved playback position: a movie, or a single episode of a series.
/// Entries are written to local storage only; nothing leaves the machine.
/// </summary>
public sealed class WatchProgressEntry : ObservableObject
{
    /// <summary>Positions below this are treated as "just opened it".</summary>
    public const long MinimumPositionMs = 20_000;

    /// <summary>Anything this far into the title counts as finished.</summary>
    public const double CompletedFraction = 0.95;

    [JsonPropertyName("metaId")]
    public string MetaId { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "movie";

    [JsonPropertyName("videoId")]
    public string VideoId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("poster")]
    public string? Poster { get; set; }

    [JsonPropertyName("background")]
    public string? Background { get; set; }

    [JsonPropertyName("episodeTitle")]
    public string? EpisodeTitle { get; set; }

    [JsonPropertyName("season")]
    public int Season { get; set; }

    [JsonPropertyName("episode")]
    public int Episode { get; set; }

    [JsonPropertyName("positionMs")]
    public long PositionMs { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("completed")]
    public bool Completed { get; set; }

    /// <summary>
    /// A placeholder row for the next unwatched episode of a partly watched
    /// series. It has no playback position and only feeds Continue watching.
    /// </summary>
    [JsonPropertyName("nextUp")]
    public bool NextUp { get; set; }

    /// <summary>
    /// When this movie or episode was watched. Written by playback completion
    /// and by an explicit "mark as watched", so a later rewatch never loses the
    /// watched mark. Kept as a timestamp because an external history service
    /// (Trakt and friends) needs one when we sync in the future.
    /// </summary>
    [JsonPropertyName("watchedAt")]
    public DateTimeOffset? WatchedAt { get; set; }

    /// <summary>IMDb id when the Stremio id is one, ready for future sync.</summary>
    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; set; }

    /// <summary>
    /// Total episodes known for the series when this row was written. Lets a
    /// poster card say "Watched" only when the whole series is finished.
    /// </summary>
    [JsonPropertyName("episodeCount")]
    public int EpisodeCount { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    // ----- Remembered stream source -------------------------------------

    /// <summary>How long a remembered source is preferred over a fresh search.</summary>
    public static readonly TimeSpan SourceMaxAge = TimeSpan.FromHours(24);

    [JsonPropertyName("sourceUrl")]
    public string? SourceUrl { get; set; }

    [JsonPropertyName("sourceAddon")]
    public string? SourceAddon { get; set; }

    [JsonPropertyName("sourceAddonUrl")]
    public string? SourceAddonUrl { get; set; }

    [JsonPropertyName("sourceQuality")]
    public string? SourceQuality { get; set; }

    [JsonPropertyName("sourceHeaders")]
    public Dictionary<string, string>? SourceHeaders { get; set; }

    [JsonPropertyName("sourceSavedAt")]
    public DateTimeOffset SourceSavedAt { get; set; }

    private BitmapImage? _posterImage;

    [JsonIgnore]
    public bool IsSeries => Season > 0 || Episode > 0;

    /// <summary>True once the title has ever been watched, even while rewatching.</summary>
    [JsonIgnore]
    public bool IsWatched => WatchedAt is not null || Completed;

    [JsonIgnore]
    public string EpisodeTag => IsSeries ? $"S{Season:00}E{Episode:00}" : "";

    [JsonIgnore]
    public double ProgressFraction => DurationMs <= 0
        ? 0
        : Math.Clamp((double)PositionMs / DurationMs, 0, 1);

    [JsonIgnore]
    public long RemainingMs => Math.Max(0, DurationMs - PositionMs);

    [JsonIgnore]
    public string RemainingText
    {
        get
        {
            if (NextUp)
            {
                return "Next episode";
            }

            if (Completed)
            {
                return "Watched";
            }

            if (DurationMs <= 0)
            {
                return "In progress";
            }

            if (RemainingMs < 60_000)
            {
                return "Less than a minute left";
            }

            return $"{Math.Max(1, (int)Math.Ceiling(RemainingMs / 60_000.0))} min left";
        }
    }

    /// <summary>Card subtitle: "S02E04 · 18 min left" for series, otherwise just the time left.</summary>
    [JsonIgnore]
    public string SubtitleLine => IsSeries ? $"{EpisodeTag} · {RemainingText}" : RemainingText;

    /// <summary>Details page marker: "Watched" or "Resume 23:41".</summary>
    [JsonIgnore]
    public string ResumeLabel => Completed
        ? "Watched"
        : $"Resume {FormatPosition(PositionMs)}";

    [JsonIgnore]
    public bool HasSource => !string.IsNullOrWhiteSpace(SourceUrl);

    /// <summary>True when the remembered source is still worth trying first.</summary>
    [JsonIgnore]
    public bool IsSourceFresh =>
        HasSource && SourceSavedAt > DateTimeOffset.UtcNow - SourceMaxAge;

    [JsonIgnore]
    public BitmapImage? PosterImage => _posterImage ??= ImageLoader.Load(Poster, 360);

    [JsonIgnore]
    public bool HasPoster => PosterImage is not null;

    public PlaybackRequest ToPlaybackRequest()
    {
        var meta = new MetaItem
        {
            Id = MetaId,
            Type = Type,
            Name = Name,
            Poster = Poster,
            Background = Background,
        };

        var subtitle = IsSeries
            ? string.IsNullOrWhiteSpace(EpisodeTitle)
                ? EpisodeTag
                : $"{EpisodeTag} · {EpisodeTitle}"
            : "";

        return new PlaybackRequest(
            Type,
            VideoId,
            Name,
            subtitle,
            meta,
            EpisodeTitle,
            Season,
            Episode);
    }

    public static string FormatPosition(long milliseconds)
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

    public static string? DeriveImdbId(string? metaId) =>
        metaId is { Length: > 2 } id && id.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
            ? id
            : null;

    public override string ToString() =>
        IsSeries ? $"{Name} {EpisodeTag}" : Name;
}

/// <summary>
/// How much of a title has been watched. A movie is either watched or not; a
/// series is watched only when every episode we know about is marked watched.
/// </summary>
public enum TitleWatchState
{
    None,
    InProgress,
    PartiallyWatched,
    Watched,
}

/// <summary>Aggregated watch state for one movie or series.</summary>
public readonly record struct TitleWatchSummary(
    TitleWatchState State,
    int WatchedCount,
    int EpisodeCount)
{
    public bool HasProgress => State != TitleWatchState.None;

    public bool IsWatched => State == TitleWatchState.Watched;

    /// <summary>Compact label for poster overlays and the details hero.</summary>
    public string BadgeText => State switch
    {
        TitleWatchState.Watched => "Watched",
        TitleWatchState.PartiallyWatched when EpisodeCount > 0 =>
            $"{Math.Min(WatchedCount, EpisodeCount)}/{EpisodeCount} watched",
        TitleWatchState.PartiallyWatched => "Partially watched",
        TitleWatchState.InProgress => "Watching",
        _ => "",
    };
}

/// <summary>
/// The stream source that is currently playing, remembered so the next
/// continue-watching attempt can start immediately.
/// </summary>
public sealed record PlaybackSourceInfo(
    string Url,
    string AddonName,
    string? AddonUrl,
    string QualityLabel,
    IReadOnlyDictionary<string, string>? Headers);

/// <summary>
/// The local watch history: one row per movie or episode with the last
/// position, so the player can resume and Home can offer "Continue watching".
/// Stored in <c>watch-progress.json</c> next to the addon list.
/// </summary>
public sealed class WatchProgressStore
{
    /// <summary>Resume positions kept for titles that are not watched yet.</summary>
    private const int MaxEntries = 500;

    /// <summary>
    /// Watched marks are history, not spare resume rows. Keep far more of them
    /// so a long series cannot silently evict another title's watch history.
    /// </summary>
    private const int MaxWatchedEntries = 5_000;

    /// <summary>A seek further back than this is deliberate and is saved.</summary>
    private const long RewindToleranceMs = 60_000;

    private readonly object _loadLock = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Task? _loadTask;
    private string? _storagePath;

    public ObservableCollection<WatchProgressEntry> Entries { get; } = [];

    /// <summary>Raised after the list changed and was persisted.</summary>
    public event EventHandler? Changed;

    public Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        Task load;
        lock (_loadLock)
        {
            load = _loadTask ??= LoadCoreAsync();
        }

        return load.WaitAsync(cancellationToken);
    }

    /// <summary>Saved position for one movie or episode, if any.</summary>
    public WatchProgressEntry? Find(string? videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        return Entries.FirstOrDefault(entry =>
            entry.VideoId.Equals(videoId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The newest unfinished episode (or movie) for a title, used for the
    /// details page resume button.
    /// </summary>
    public WatchProgressEntry? FindResume(string? metaId)
    {
        var latest = Entries
            .Where(entry =>
                entry.MetaId.Equals(metaId ?? "", StringComparison.OrdinalIgnoreCase) &&
                !entry.NextUp)
            .OrderByDescending(entry => entry.UpdatedAt)
            .FirstOrDefault();

        return latest is { Completed: false } && latest.PositionMs > 0 ? latest : null;
    }

    /// <summary>
    /// One card per title, newest first: the latest episode for a series and
    /// the movie itself. Finished titles are left out.
    /// </summary>
    public IReadOnlyList<WatchProgressEntry> GetContinueWatching(int limit = 20) =>
        Entries
            .GroupBy(entry => entry.MetaId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .Where(entry => entry.NextUp || (!entry.Completed && entry.ProgressFraction > 0.01))
                .OrderBy(entry => entry.NextUp ? 1 : 0)
                .ThenByDescending(entry => entry.UpdatedAt)
                .FirstOrDefault())
            .OfType<WatchProgressEntry>()
            .OrderByDescending(entry => entry.UpdatedAt)
            .Take(limit)
            .ToList();

    /// <summary>Saved rows for one title keyed by season and episode.</summary>
    public IReadOnlyDictionary<string, WatchProgressEntry> GetEpisodeEntries(string? metaId) =>
        Entries
            .Where(entry =>
                entry.MetaId.Equals(metaId ?? "", StringComparison.OrdinalIgnoreCase) &&
                !entry.NextUp)
            .GroupBy(entry => EpisodeKey(entry.Season, entry.Episode))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(entry => entry.UpdatedAt).First(),
                StringComparer.Ordinal);

    public static string EpisodeKey(int season, int episode) => $"{season}:{episode}";

    /// <summary>True when this exact movie or episode has been watched.</summary>
    public bool IsWatched(string? videoId) => Find(videoId)?.IsWatched == true;

    /// <summary>
    /// Aggregate watch state for a title. Movies are one row; series count
    /// watched episodes against the episode count recorded with each row.
    /// </summary>
    public TitleWatchSummary GetTitleState(string? metaId, string? type = null)
    {
        if (string.IsNullOrWhiteSpace(metaId))
        {
            return default;
        }

        var entries = Entries
            .Where(entry => entry.MetaId.Equals(metaId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (entries.Count == 0)
        {
            return default;
        }

        var isSeries = IsSeriesType(type) || entries.Any(entry => entry.IsSeries);
        if (!isSeries)
        {
            var movie = entries.FirstOrDefault(entry =>
                    entry.VideoId.Equals(metaId, StringComparison.OrdinalIgnoreCase))
                ?? entries[0];

            return movie.IsWatched
                ? new TitleWatchSummary(TitleWatchState.Watched, 1, 1)
                : movie.PositionMs > 0
                    ? new TitleWatchSummary(TitleWatchState.InProgress, 0, 1)
                    : default;
        }

        var watchedCount = entries.Count(entry => entry.IsWatched && entry.IsSeries);
        var knownCount = entries.Max(entry => entry.EpisodeCount);
        var inProgress = entries.Any(entry =>
            !entry.IsWatched && entry.PositionMs > 0);

        if (knownCount > 0 && watchedCount >= knownCount)
        {
            return new TitleWatchSummary(TitleWatchState.Watched, watchedCount, knownCount);
        }

        if (watchedCount > 0)
        {
            return new TitleWatchSummary(TitleWatchState.PartiallyWatched, watchedCount, knownCount);
        }

        return inProgress
            ? new TitleWatchSummary(TitleWatchState.InProgress, 0, knownCount)
            : default;
    }

    /// <summary>Stamps the local watched state onto catalog cards.</summary>
    public void ApplyWatchState(IEnumerable<MetaItem>? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items)
        {
            ApplyWatchState(item);
        }
    }

    public void ApplyWatchState(MetaItem item)
    {
        var summary = GetTitleState(item.Id, item.Type);
        item.ApplyWatchState(
            summary.State == TitleWatchState.Watched,
            summary.State is TitleWatchState.PartiallyWatched or TitleWatchState.InProgress,
            summary.BadgeText);
    }

    private static bool IsSeriesType(string? type) =>
        type is not null &&
        (type.Equals("series", StringComparison.OrdinalIgnoreCase) ||
         type.Equals("tv", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Records the current position. Short starts are ignored so merely opening
    /// a title doesn't create history, and an existing position is never
    /// rewound by a few seconds of playback.
    /// </summary>
    public async Task SaveAsync(
        PlaybackRequest request,
        long positionMs,
        long durationMs,
        bool completed = false,
        PlaybackSourceInfo? source = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.VideoId))
        {
            return;
        }

        await EnsureLoadedAsync(cancellationToken);

        positionMs = Math.Max(0, positionMs);
        if (durationMs > 0)
        {
            positionMs = Math.Min(positionMs, durationMs);
        }

        var finished = completed ||
                       (durationMs > 0 && positionMs >= durationMs * WatchProgressEntry.CompletedFraction);
        var existing = Find(request.VideoId);

        if (!finished && positionMs < WatchProgressEntry.MinimumPositionMs)
        {
            // Don't let a quick open or a rewatch wipe a real position.
            return;
        }

        if (!finished && existing is not null && !existing.Completed &&
            positionMs < existing.PositionMs &&
            existing.PositionMs - positionMs < RewindToleranceMs)
        {
            return;
        }

        var metaId = string.IsNullOrWhiteSpace(request.Meta.Id) ? request.VideoId : request.Meta.Id;
        RemoveNextUpEntries(metaId, exceptVideoId: request.VideoId);

        var entry = new WatchProgressEntry
        {
            MetaId = metaId,
            Type = request.Type,
            VideoId = request.VideoId,
            Name = request.Title,
            Poster = request.Meta.Poster,
            Background = request.Meta.Background,
            EpisodeTitle = request.EpisodeTitle,
            Season = request.Season,
            Episode = request.Episode,
            PositionMs = finished && durationMs > 0 ? durationMs : positionMs,
            DurationMs = durationMs > 0 ? durationMs : existing?.DurationMs ?? 0,
            Completed = finished,
            NextUp = false,
            WatchedAt = finished
                ? existing?.WatchedAt ?? DateTimeOffset.UtcNow
                : existing?.WatchedAt,
            ImdbId = WatchProgressEntry.DeriveImdbId(
                         string.IsNullOrWhiteSpace(request.Meta.Id) ? request.VideoId : request.Meta.Id)
                     ?? existing?.ImdbId,
            EpisodeCount = ResolveEpisodeCount(request, existing),
            UpdatedAt = DateTimeOffset.UtcNow,
            SourceUrl = source?.Url ?? existing?.SourceUrl,
            SourceAddon = source?.AddonName ?? existing?.SourceAddon,
            SourceAddonUrl = source?.AddonUrl ?? existing?.SourceAddonUrl,
            SourceQuality = source?.QualityLabel ?? existing?.SourceQuality,
            SourceHeaders = source?.Headers is null
                ? existing?.SourceHeaders
                : new Dictionary<string, string>(source.Headers, StringComparer.OrdinalIgnoreCase),
            SourceSavedAt = source is null ? existing?.SourceSavedAt ?? default : DateTimeOffset.UtcNow,
        };

        var index = existing is null ? -1 : Entries.IndexOf(existing);
        if (index >= 0)
        {
            Entries.RemoveAt(index);
        }

        Entries.Insert(0, entry);
        Trim();

        await SaveCoreAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Marks a whole movie or one episode watched or unwatched. Marking watched
    /// writes a timestamp instead of a fake playback position, so the details
    /// page and any future external sync both see a real watch event.
    /// </summary>
    public Task SetMovieWatchedAsync(
        MetaItem meta,
        bool watched,
        CancellationToken cancellationToken = default) =>
        SetRequestsWatchedAsync([PlaybackRequest.ForMeta(meta)], watched, 0, cancellationToken);

    public Task SetEpisodeWatchedAsync(
        MetaItem series,
        MetaVideo episode,
        bool watched,
        CancellationToken cancellationToken = default) =>
        SetRequestsWatchedAsync(
            [PlaybackRequest.ForEpisode(series, episode)],
            watched,
            CountEpisodes(series),
            cancellationToken);

    /// <summary>Marks any selection of episodes watched or unwatched at once.</summary>
    public Task SetEpisodesWatchedAsync(
        MetaItem series,
        IEnumerable<MetaVideo> episodes,
        bool watched,
        CancellationToken cancellationToken = default)
    {
        var count = CountEpisodes(series);
        var requests = episodes
            .Where(episode => episode.EpisodeNumber > 0)
            .Select(episode => PlaybackRequest.ForEpisode(series, episode))
            .ToList();

        return SetRequestsWatchedAsync(requests, watched, count, cancellationToken);
    }

    private async Task SetRequestsWatchedAsync(
        IReadOnlyCollection<PlaybackRequest> requests,
        bool watched,
        int episodeCount,
        CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);

        var changed = false;
        foreach (var request in requests)
        {
            if (string.IsNullOrWhiteSpace(request.VideoId))
            {
                continue;
            }

            var existing = Find(request.VideoId);

            if (!watched)
            {
                // Only remove a real watched mark. A row that is merely in
                // progress (possibly with a remembered source) was never
                // watched, so "mark unwatched" must not erase its resume data.
                if (existing is { IsWatched: true })
                {
                    Entries.Remove(existing);
                    changed = true;
                }

                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var entry = new WatchProgressEntry
            {
                MetaId = string.IsNullOrWhiteSpace(request.Meta.Id) ? request.VideoId : request.Meta.Id,
                Type = request.Type,
                VideoId = request.VideoId,
                Name = request.Title,
                Poster = request.Meta.Poster,
                Background = request.Meta.Background,
                EpisodeTitle = request.EpisodeTitle,
                Season = request.Season,
                Episode = request.Episode,
                PositionMs = existing is { PositionMs: > 0 } ? existing.PositionMs : 0,
                DurationMs = existing?.DurationMs ?? 0,
                Completed = true,
                WatchedAt = existing?.WatchedAt ?? now,
                ImdbId = WatchProgressEntry.DeriveImdbId(
                             string.IsNullOrWhiteSpace(request.Meta.Id) ? request.VideoId : request.Meta.Id)
                         ?? existing?.ImdbId,
                EpisodeCount = episodeCount > 0 ? episodeCount : existing?.EpisodeCount ?? 0,
                UpdatedAt = now,
                SourceUrl = existing?.SourceUrl,
                SourceAddon = existing?.SourceAddon,
                SourceAddonUrl = existing?.SourceAddonUrl,
                SourceQuality = existing?.SourceQuality,
                SourceHeaders = existing?.SourceHeaders,
                SourceSavedAt = existing?.SourceSavedAt ?? default,
            };

            var index = existing is null ? -1 : Entries.IndexOf(existing);
            if (index >= 0)
            {
                Entries.RemoveAt(index);
            }

            Entries.Insert(0, entry);
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        Trim();
        await SaveCoreAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static int CountEpisodes(MetaItem series) =>
        series.Videos?.Count(video => video.EpisodeNumber > 0) ?? 0;

    /// <summary>
    /// Keeps one Continue watching placeholder for the next unwatched episode
    /// of a partly watched series. Called after an explicit watched change, so
    /// marking episodes 1-8 of 10 offers episode 9 instead of dropping the show
    /// from the rail. An actual in-progress episode always wins over this row.
    /// </summary>
    public async Task ReconcileNextUpAsync(
        MetaItem series,
        CancellationToken cancellationToken = default)
    {
        if (!IsSeriesType(series.Type) || series.Videos is not { Count: > 0 } videos)
        {
            return;
        }

        var episodes = videos
            .Where(video => video.EpisodeNumber > 0)
            .OrderBy(video => video.Season == 0 ? int.MaxValue : video.Season)
            .ThenBy(video => video.EpisodeNumber)
            .ToList();

        if (episodes.Count == 0)
        {
            return;
        }

        await EnsureLoadedAsync(cancellationToken);

        var metaId = series.Id;
        var entries = Entries
            .Where(entry => entry.MetaId.Equals(metaId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var existingNext = entries.FirstOrDefault(entry => entry.NextUp);
        var hasInProgress = entries.Any(entry =>
            !entry.NextUp && !entry.IsWatched && entry.PositionMs > 0);
        var anyWatched = entries.Any(entry => entry.IsWatched && entry.IsSeries);

        MetaVideo? nextEpisode = null;
        if (anyWatched && !hasInProgress)
        {
            var watchedKeys = entries
                .Where(entry => entry.IsWatched && entry.IsSeries)
                .Select(entry => EpisodeKey(entry.Season, entry.Episode))
                .ToHashSet(StringComparer.Ordinal);

            nextEpisode = episodes.FirstOrDefault(episode =>
                !watchedKeys.Contains(EpisodeKey(episode.Season, episode.EpisodeNumber)));
        }

        var nextKey = nextEpisode is null
            ? null
            : EpisodeKey(nextEpisode.Season, nextEpisode.EpisodeNumber);
        var existingKey = existingNext is null
            ? null
            : EpisodeKey(existingNext.Season, existingNext.Episode);

        if (string.Equals(nextKey, existingKey, StringComparison.Ordinal))
        {
            return;
        }

        if (existingNext is not null)
        {
            Entries.Remove(existingNext);
        }

        if (nextEpisode is not null)
        {
            var request = PlaybackRequest.ForEpisode(series, nextEpisode);
            var now = DateTimeOffset.UtcNow;
            Entries.Insert(0, new WatchProgressEntry
            {
                MetaId = metaId,
                Type = request.Type,
                VideoId = request.VideoId,
                Name = request.Title,
                Poster = request.Meta.Poster,
                Background = request.Meta.Background,
                EpisodeTitle = request.EpisodeTitle,
                Season = request.Season,
                Episode = request.Episode,
                PositionMs = 0,
                DurationMs = 0,
                Completed = false,
                NextUp = true,
                ImdbId = WatchProgressEntry.DeriveImdbId(metaId),
                EpisodeCount = episodes.Count,
                UpdatedAt = now,
            });
        }

        Trim();
        await SaveCoreAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveNextUpEntries(string metaId, string? exceptVideoId = null)
    {
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            var entry = Entries[i];
            if (!entry.NextUp ||
                !entry.MetaId.Equals(metaId, StringComparison.OrdinalIgnoreCase) ||
                (exceptVideoId is not null &&
                 entry.VideoId.Equals(exceptVideoId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Entries.RemoveAt(i);
        }
    }

    /// <summary>
    /// Records how many episodes the addon listed for a series. Older history
    /// files have no count, and a series watched before this feature existed
    /// would otherwise stay stuck at "Partially watched".
    /// </summary>
    public async Task RememberEpisodeCountAsync(
        string? metaId,
        int episodeCount,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(metaId) || episodeCount <= 0)
        {
            return;
        }

        await EnsureLoadedAsync(cancellationToken);

        var changed = false;
        foreach (var entry in Entries.Where(entry =>
                     entry.MetaId.Equals(metaId, StringComparison.OrdinalIgnoreCase) &&
                     entry.EpisodeCount < episodeCount))
        {
            entry.EpisodeCount = episodeCount;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        await SaveCoreAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static int ResolveEpisodeCount(PlaybackRequest request, WatchProgressEntry? existing)
    {
        var known = request.Meta.Videos?.Count(video => video.EpisodeNumber > 0) ?? 0;
        return known > 0 ? known : existing?.EpisodeCount ?? 0;
    }

    private void Trim()
    {
        // Resume rows stay small and disposable. Watched marks are history and
        // get a much larger budget, because trimming them would lose data that
        // a later external sync may need.
        var unwatched = Entries
            .Where(entry => !entry.IsWatched)
            .OrderBy(entry => entry.UpdatedAt)
            .ToList();

        while (unwatched.Count > MaxEntries)
        {
            Entries.Remove(unwatched[0]);
            unwatched.RemoveAt(0);
        }

        var watched = Entries
            .Where(entry => entry.IsWatched)
            .OrderBy(entry => entry.UpdatedAt)
            .ToList();

        while (watched.Count > MaxWatchedEntries)
        {
            Entries.Remove(watched[0]);
            watched.RemoveAt(0);
        }
    }

    /// <summary>Forgets the remembered source after it failed to play.</summary>
    public async Task ForgetSourceAsync(string? videoId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return;
        }

        await EnsureLoadedAsync(cancellationToken);

        var entry = Find(videoId);
        if (entry is not { HasSource: true })
        {
            return;
        }

        entry.SourceUrl = null;
        entry.SourceAddon = null;
        entry.SourceAddonUrl = null;
        entry.SourceQuality = null;
        entry.SourceHeaders = null;
        entry.SourceSavedAt = default;

        await SaveCoreAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Removes a title from Continue watching without losing its watched mark.
    /// Ordinary in-progress rows are dropped; a watched title that was being
    /// rewatched is reset to "watched" instead of disappearing from history.
    /// </summary>
    public async Task DismissContinueWatchingAsync(
        string? metaId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(metaId))
        {
            return;
        }

        await EnsureLoadedAsync(cancellationToken);

        var changed = false;
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            var entry = Entries[i];
            if (!entry.MetaId.Equals(metaId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.IsWatched)
            {
                if (entry.Completed && entry.PositionMs == 0)
                {
                    continue;
                }

                entry.Completed = true;
                entry.PositionMs = 0;
                entry.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                Entries.RemoveAt(i);
            }

            changed = true;
        }

        if (!changed)
        {
            return;
        }

        await SaveCoreAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets every saved position for one title (all its episodes).</summary>
    public async Task RemoveTitleAsync(string? metaId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(metaId))
        {
            return;
        }

        await EnsureLoadedAsync(cancellationToken);

        var removed = false;
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (Entries[i].MetaId.Equals(metaId, StringComparison.OrdinalIgnoreCase))
            {
                Entries.RemoveAt(i);
                removed = true;
            }
        }

        if (!removed)
        {
            return;
        }

        await SaveCoreAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        if (Entries.Count == 0)
        {
            return;
        }

        Entries.Clear();
        await SaveCoreAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadCoreAsync()
    {
        var path = GetStoragePath();

        try
        {
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path);
                var entries = JsonSerializer.Deserialize<List<WatchProgressEntry>>(json) ?? [];

                foreach (var entry in entries
                             .Where(entry => !string.IsNullOrWhiteSpace(entry.VideoId))
                             .OrderByDescending(entry => entry.UpdatedAt))
                {
                    // Files written before watched marks existed only carry
                    // "completed"; give those rows a timestamp so the new
                    // watched state and a future Trakt sync see a real event.
                    if (entry.Completed && entry.WatchedAt is null)
                    {
                        entry.WatchedAt = entry.UpdatedAt == default
                            ? DateTimeOffset.UtcNow
                            : entry.UpdatedAt;
                    }

                    entry.ImdbId ??= WatchProgressEntry.DeriveImdbId(entry.MetaId);
                    Entries.Add(entry);
                }

                Trim();
            }
        }
        catch
        {
            // A damaged history file must not block playback.
            Entries.Clear();
        }
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var path = GetStoragePath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(
                    Entries.ToList(),
                    new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
        }
        catch
        {
            // History stays in memory if persistence fails.
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private string GetStoragePath()
    {
        if (_storagePath is not null)
        {
            return _storagePath;
        }

        _storagePath = Path.Combine(LocalPaths.Root, "watch-progress.json");

        return _storagePath;
    }
}

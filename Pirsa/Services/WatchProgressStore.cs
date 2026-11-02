using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Pirsa.Models;
using Windows.Storage;

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

    public override string ToString() =>
        IsSeries ? $"{Name} {EpisodeTag}" : Name;
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
    private const int MaxEntries = 500;

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
            .Where(entry => entry.MetaId.Equals(metaId ?? "", StringComparison.OrdinalIgnoreCase))
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
            .Select(group => group.OrderByDescending(entry => entry.UpdatedAt).First())
            .Where(entry => !entry.Completed && entry.ProgressFraction > 0.01)
            .OrderByDescending(entry => entry.UpdatedAt)
            .Take(limit)
            .ToList();

    /// <summary>Saved rows for one title keyed by season and episode.</summary>
    public IReadOnlyDictionary<string, WatchProgressEntry> GetEpisodeEntries(string? metaId) =>
        Entries
            .Where(entry => entry.MetaId.Equals(metaId ?? "", StringComparison.OrdinalIgnoreCase))
            .GroupBy(entry => EpisodeKey(entry.Season, entry.Episode))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(entry => entry.UpdatedAt).First(),
                StringComparer.Ordinal);

    public static string EpisodeKey(int season, int episode) => $"{season}:{episode}";

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
            PositionMs = finished && durationMs > 0 ? durationMs : positionMs,
            DurationMs = durationMs > 0 ? durationMs : existing?.DurationMs ?? 0,
            Completed = finished,
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

        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }

        await SaveCoreAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
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
                             .OrderByDescending(entry => entry.UpdatedAt)
                             .Take(MaxEntries))
                {
                    Entries.Add(entry);
                }
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

        try
        {
            _storagePath = Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                "watch-progress.json");
        }
        catch
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Pirsa");
            _storagePath = Path.Combine(directory, "watch-progress.json");
        }

        return _storagePath;
    }
}

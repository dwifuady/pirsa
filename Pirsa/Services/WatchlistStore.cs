using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Pirsa.Models;

namespace Pirsa.Services;

/// <summary>
/// One title saved to the local watchlist. Only what a poster card and the
/// details page need is kept; full metadata is re-fetched from the addons.
/// </summary>
public sealed class WatchlistEntry : ObservableObject
{
    [JsonPropertyName("metaId")]
    public string MetaId { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("poster")]
    public string? Poster { get; set; }

    [JsonPropertyName("background")]
    public string? Background { get; set; }

    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    [JsonPropertyName("releaseInfo")]
    public string? ReleaseInfo { get; set; }

    [JsonPropertyName("imdbRating")]
    public string? ImdbRating { get; set; }

    /// <summary>Manifest URL of the addon that returned this title, when known.</summary>
    [JsonPropertyName("addonUrl")]
    public string? SourceAddonUrl { get; set; }

    [JsonPropertyName("addedAt")]
    public DateTimeOffset AddedAt { get; set; }

    private BitmapImage? _posterImage;
    private bool _isWatched;
    private bool _isPartiallyWatched;
    private string _watchBadgeText = "";

    [JsonIgnore]
    public bool IsWatched
    {
        get => _isWatched;
        private set => SetProperty(ref _isWatched, value);
    }

    [JsonIgnore]
    public bool IsPartiallyWatched
    {
        get => _isPartiallyWatched;
        private set => SetProperty(ref _isPartiallyWatched, value);
    }

    [JsonIgnore]
    public string WatchBadgeText
    {
        get => _watchBadgeText;
        private set
        {
            if (SetProperty(ref _watchBadgeText, value))
            {
                OnPropertyChanged(nameof(HasWatchBadge));
            }
        }
    }

    [JsonIgnore]
    public bool HasWatchBadge => !string.IsNullOrWhiteSpace(WatchBadgeText);

    public void ApplyWatchState(bool isWatched, bool isPartiallyWatched, string badgeText)
    {
        IsWatched = isWatched;
        IsPartiallyWatched = isPartiallyWatched;
        WatchBadgeText = badgeText;
    }

    [JsonIgnore]
    public BitmapImage? PosterImage =>
        _posterImage ??= ImageLoader.Load(ImageLoader.UpgradePoster(Poster), 360);

    [JsonIgnore]
    public bool HasPoster => !string.IsNullOrWhiteSpace(Poster);

    [JsonIgnore]
    public string TypeLabel => Type.ToLowerInvariant() switch
    {
        "movie" => "Movie",
        "series" => "Series",
        "tv" => "Live TV",
        "channel" => "Live TV",
        "music" => "Music",
        "podcast" => "Podcast",
        _ => string.IsNullOrWhiteSpace(Type) ? "Title" : Type,
    };

    [JsonIgnore]
    public string CardMeta
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ReleaseInfo)) parts.Add(ReleaseInfo!);
            if (!string.IsNullOrWhiteSpace(ImdbRating)) parts.Add($"★ {ImdbRating}");
            return string.Join(" · ", parts);
        }
    }

    [JsonIgnore]
    public string AddedText =>
        AddedAt == default ? "" : $"Added {AddedAt.ToLocalTime():MMM d, yyyy}";

    /// <summary>
    /// Catalog-style preview handed to the details page, which refreshes it
    /// from the addons on arrival.
    /// </summary>
    public MetaItem ToMetaItem() => new()
    {
        Id = MetaId,
        Type = Type,
        Name = Name,
        Poster = Poster,
        Background = Background,
        Logo = Logo,
        ReleaseInfo = ReleaseInfo,
        ImdbRating = ImdbRating,
        SourceAddonUrl = SourceAddonUrl,
    };

    public override string ToString() => Name;
}

/// <summary>
/// The local watchlist: one row per saved movie, series or other title.
/// Stored in <c>watchlist.json</c> next to the watch history, so nothing
/// leaves the machine.
/// </summary>
public sealed class WatchlistStore
{
    private const int MaxEntries = 500;

    private readonly object _loadLock = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Task? _loadTask;
    private string? _storagePath;

    /// <summary>Saved titles, newest first.</summary>
    public ObservableCollection<WatchlistEntry> Entries { get; } = [];

    /// <summary>Raised after the list changed and was persisted.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised when the user removes a title or clears the list. Sync turns this
    /// into a tombstone so another device's copy cannot bring it back.
    /// </summary>
    public event EventHandler<WatchlistEntry>? Removed;

    public int Count => Entries.Count;

    public Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        Task load;
        lock (_loadLock)
        {
            load = _loadTask ??= LoadCoreAsync();
        }

        return load.WaitAsync(cancellationToken);
    }

    public WatchlistEntry? Find(string? metaId)
    {
        if (string.IsNullOrWhiteSpace(metaId))
        {
            return null;
        }

        return Entries.FirstOrDefault(entry =>
            entry.MetaId.Equals(metaId, StringComparison.OrdinalIgnoreCase));
    }

    public bool Contains(string? metaId) => Find(metaId) is not null;

    /// <summary>
    /// Adds a title to the watchlist. Returns false when it was already saved.
    /// </summary>
    public async Task<bool> AddAsync(MetaItem meta, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(meta.Id))
        {
            return false;
        }

        await EnsureLoadedAsync(cancellationToken);

        if (Contains(meta.Id))
        {
            return false;
        }

        Entries.Insert(0, new WatchlistEntry
        {
            MetaId = meta.Id,
            Type = meta.Type,
            Name = meta.Name,
            Poster = meta.Poster,
            Background = meta.Background,
            Logo = meta.Logo,
            ReleaseInfo = meta.ReleaseInfo,
            ImdbRating = meta.ImdbRating,
            SourceAddonUrl = meta.SourceAddonUrl,
            AddedAt = DateTimeOffset.UtcNow,
        });

        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }

        await SaveCoreAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Removes one title. Returns false when it wasn't saved.</summary>
    public async Task<bool> RemoveAsync(string? metaId, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        if (Find(metaId) is not { } entry)
        {
            return false;
        }

        Entries.Remove(entry);
        Removed?.Invoke(this, entry);
        await SaveCoreAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Adds the title when it is missing and removes it when it is saved.
    /// Returns the new state: true means the title is in the watchlist.
    /// </summary>
    public async Task<bool> ToggleAsync(MetaItem meta, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        if (Contains(meta.Id))
        {
            await RemoveAsync(meta.Id, cancellationToken);
            return false;
        }

        await AddAsync(meta, cancellationToken);
        return Contains(meta.Id);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        if (Entries.Count == 0)
        {
            return;
        }

        var removed = Entries.ToList();
        Entries.Clear();

        foreach (var entry in removed)
        {
            Removed?.Invoke(this, entry);
        }

        await SaveCoreAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Adds or re-adds a title that came from a sync provider. Keeps the local
    /// list newest-first and never removes a locally newer entry.
    /// </summary>
    public async Task<bool> ApplyRemoteAsync(
        string metaId,
        string type,
        string? name,
        string? poster,
        DateTimeOffset addedAt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(metaId))
        {
            return false;
        }

        await EnsureLoadedAsync(cancellationToken);

        var existing = Find(metaId);
        if (existing is not null)
        {
            if (existing.AddedAt >= addedAt)
            {
                return false;
            }

            Entries.Remove(existing);
        }

        var entry = new WatchlistEntry
        {
            MetaId = metaId,
            Type = string.IsNullOrWhiteSpace(type) ? "movie" : type,
            Name = string.IsNullOrWhiteSpace(name) ? metaId : name,
            Poster = poster,
            AddedAt = addedAt,
        };

        var index = 0;
        while (index < Entries.Count && Entries[index].AddedAt > addedAt)
        {
            index++;
        }

        Entries.Insert(index, entry);

        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }

        await SaveCoreAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Removes a title because another device removed it.</summary>
    public async Task<bool> ApplyRemoteRemoveAsync(
        string metaId,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        if (Find(metaId) is not { } entry)
        {
            return false;
        }

        Entries.Remove(entry);
        await SaveCoreAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async Task LoadCoreAsync()
    {
        var path = GetStoragePath();

        try
        {
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path);
                var entries = JsonSerializer.Deserialize<List<WatchlistEntry>>(json) ?? [];

                foreach (var entry in entries
                             .Where(entry => !string.IsNullOrWhiteSpace(entry.MetaId))
                             .OrderByDescending(entry => entry.AddedAt)
                             .Take(MaxEntries))
                {
                    Entries.Add(entry);
                }
            }
        }
        catch
        {
            // A damaged watchlist file must not break the rest of the app.
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
            // The list stays in memory if persistence fails.
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

        _storagePath = Path.Combine(LocalPaths.Root, "watchlist.json");
        return _storagePath;
    }
}

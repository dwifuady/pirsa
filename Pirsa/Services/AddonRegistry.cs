using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Pirsa.Models;

namespace Pirsa.Services;

/// <summary>
/// One configured Stremio addon. Catalogs and streams are both sourced from
/// these entries; only Cinemeta exists on a fresh install.
/// </summary>
public sealed partial class AddonEntry : ObservableObject
{
    private readonly SemaphoreSlim _manifestGate = new(1, 1);

    public AddonEntry(string manifestUrl)
    {
        Client = new StremioAddonClient(manifestUrl);
        ManifestUrl = Client.ManifestUrl;
        IsEnabled = true;
    }

    public StremioAddonClient Client { get; }

    public string ManifestUrl { get; }

    public string Host =>
        Uri.TryCreate(ManifestUrl, UriKind.Absolute, out var uri) ? uri.Host : ManifestUrl;

    [ObservableProperty]
    public partial AddonManifest? Manifest { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? LoadError { get; set; }

    /// <summary>Disabled addons stay configured but are never queried.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>1-based position in the user's addon list.</summary>
    [ObservableProperty]
    public partial int Order { get; set; }

    [ObservableProperty]
    public partial bool CanMoveUp { get; set; }

    [ObservableProperty]
    public partial bool CanMoveDown { get; set; }

    /// <summary>False while the settings list is filtered, so rows cannot be moved.</summary>
    [ObservableProperty]
    public partial bool CanReorder { get; set; }

    public string Name => Manifest?.Name is { Length: > 0 } name ? name : Host;

    public string VersionText =>
        string.IsNullOrWhiteSpace(Manifest?.Version) ? "" : $"v{Manifest.Version}";

    public string DescriptionText => Manifest?.Description ?? "";

    public bool HasDescription => !string.IsNullOrWhiteSpace(DescriptionText);

    public bool IsReady => Manifest is not null;

    public bool HasError => !string.IsNullOrWhiteSpace(LoadError);

    public bool IsDefault => AddonRegistry.IsDefaultUrl(ManifestUrl);

    public bool HasCatalogs => Manifest?.Catalogs is { Count: > 0 };

    public bool HasStreams => HasResource("stream");

    public bool HasMeta => HasResource("meta");

    public bool HasSubtitles => HasResource("subtitles");

    public string TypesText => Manifest is null
        ? ""
        : string.Join(" · ", Manifest.Types
            .Select(TypeLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    public string CapabilitiesText
    {
        get
        {
            if (Manifest is null)
            {
                return "";
            }

            var parts = new List<string>();
            if (Manifest.Catalogs.Count > 0 || HasResource("catalog")) parts.Add("Catalogs");
            if (HasStreams) parts.Add("Streams");
            if (HasMeta) parts.Add("Metadata");
            if (HasSubtitles) parts.Add("Subtitles");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Loads the manifest once; repeated calls share the result.</summary>
    public async Task<AddonManifest?> LoadManifestAsync(CancellationToken cancellationToken = default)
    {
        if (Manifest is not null)
        {
            return Manifest;
        }

        await _manifestGate.WaitAsync(cancellationToken);
        try
        {
            if (Manifest is not null)
            {
                return Manifest;
            }

            IsLoading = true;
            LoadError = null;

            try
            {
                Manifest = await Client.GetManifestAsync(cancellationToken);
                return Manifest;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LoadError = exception.Message;
                return null;
            }
            finally
            {
                IsLoading = false;
            }
        }
        finally
        {
            _manifestGate.Release();
        }
    }

    public override string ToString() => Name;

    partial void OnManifestChanged(AddonManifest? value)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(HasCatalogs));
        OnPropertyChanged(nameof(HasStreams));
        OnPropertyChanged(nameof(HasMeta));
        OnPropertyChanged(nameof(HasSubtitles));
        OnPropertyChanged(nameof(TypesText));
        OnPropertyChanged(nameof(CapabilitiesText));
        OnPropertyChanged(nameof(HasError));
    }

    partial void OnLoadErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    private bool HasResource(string name) =>
        Manifest?.Resources.Any(resource =>
            resource.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == true;

    private static string TypeLabel(string type) => type.ToLowerInvariant() switch
    {
        "movie" => "Movies",
        "series" => "Series",
        "tv" => "Live TV",
        "channel" => "Channels",
        "music" => "Music",
        "podcast" => "Podcasts",
        _ => type,
    };
}

/// <summary>
/// The user's addon list, persisted next to the app's local data. A fresh
/// install contains only Cinemeta; stream addons are added from Settings.
///
/// List order is the user's priority order: the first enabled addon wins for
/// metadata, and stream sources are requested in list order.
/// </summary>
public sealed class AddonRegistry
{
    public const string DefaultManifestUrl = "https://v3-cinemeta.strem.io/manifest.json";

    public const string SubtitleManifestUrl =
        "https://opensubtitles-v3.strem.io/manifest.json";

    private static readonly string[] DefaultManifestUrls =
    [
        DefaultManifestUrl,
        SubtitleManifestUrl,
    ];

    private static readonly JsonSerializerOptions StoreJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Task? _loadTask;
    private string? _storagePath;

    public ObservableCollection<AddonEntry> Entries { get; } = [];

    /// <summary>Bumped whenever the list changes in a way that affects content.</summary>
    public int Revision { get; private set; }

    /// <summary>Raised after any add, remove, reorder, or enable/disable.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<AddonEntry> EnabledEntries =>
        Entries.Where(entry => entry.IsEnabled).ToList();

    public AddonEntry? PreferredMetaEntry =>
        Entries.FirstOrDefault(entry => entry.IsEnabled && entry.HasMeta);

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        Task load;
        lock (_gate)
        {
            load = _loadTask ??= LoadCoreAsync();
        }

        await load.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Fetches manifests for the enabled addons in parallel. Settings asks for
    /// disabled ones too so their names and capabilities stay visible while
    /// they are off.
    /// </summary>
    public async Task LoadManifestsAsync(
        CancellationToken cancellationToken = default,
        bool includeDisabled = false)
    {
        await EnsureLoadedAsync(cancellationToken);

        var entries = includeDisabled ? Entries.ToList() : EnabledEntries;

        await Task.WhenAll(entries.Select(entry => entry.LoadManifestAsync(cancellationToken)));
    }

    public AddonEntry? GetEntry(string? manifestUrl)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return null;
        }

        return Entries.FirstOrDefault(entry => UrlEquals(entry.ManifestUrl, manifestUrl));
    }

    public AddonEntry? GetEnabledEntry(string? manifestUrl) =>
        GetEntry(manifestUrl) is { IsEnabled: true } entry ? entry : null;

    /// <summary>0-based rank of an addon, or <see cref="int.MaxValue"/> when it is gone.</summary>
    public int GetRank(string? manifestUrl)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return int.MaxValue;
        }

        for (var index = 0; index < Entries.Count; index++)
        {
            if (UrlEquals(Entries[index].ManifestUrl, manifestUrl))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    /// <summary>
    /// Enabled addons that declare meta support for this type and id prefix,
    /// in the user's priority order. Custom ids therefore only match the addon
    /// that declared them; for a custom id owned by the catalog addon, that
    /// addon is tried first even if it is lower in the list.
    /// </summary>
    public IReadOnlyList<AddonEntry> MetaCandidates(
        string type,
        string id,
        string? sourceManifestUrl = null)
    {
        var value = type ?? "";
        var idValue = id ?? "";
        var candidates = Entries
            .Where(entry => entry.IsEnabled && entry.Manifest is not null &&
                entry.Manifest.SupportsResource("meta", value, idValue))
            .ToList();

        if (candidates.Count > 1 &&
            !IsGenericId(idValue) &&
            !string.IsNullOrWhiteSpace(sourceManifestUrl))
        {
            var owner = candidates.FirstOrDefault(entry =>
                UrlEquals(entry.ManifestUrl, sourceManifestUrl));

            if (owner is not null)
            {
                candidates.Remove(owner);
                candidates.Insert(0, owner);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Ids from shared metadata ecosystems (IMDb, TMDb) are understood by many
    /// addons, so the user's metadata priority applies. Anything else is a
    /// private id that only its own addon can resolve.
    /// </summary>
    public static bool IsGenericId(string id) =>
        id.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("imdb:", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates and adds a manifest URL, then persists the list.</summary>
    public async Task<AddonEntry> AddAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        if (!TryNormalizeManifestUrl(manifestUrl, out var normalized))
        {
            throw new StremioException("Enter a valid http(s) Stremio addon manifest URL.");
        }

        var existing = Entries.FirstOrDefault(entry => UrlEquals(entry.ManifestUrl, normalized));
        if (existing is not null)
        {
            throw new StremioException(existing.IsEnabled
                ? "That addon is already configured."
                : "That addon is already configured but disabled. Turn it on in the list instead of adding it again.");
        }

        var entry = new AddonEntry(normalized);
        var manifest = await entry.LoadManifestAsync(cancellationToken);

        if (manifest is null)
        {
            throw new StremioException(entry.LoadError ?? "Couldn't load that addon's manifest.");
        }

        if (manifest.Resources.Count == 0 && manifest.Catalogs.Count == 0)
        {
            throw new StremioException("That manifest doesn't expose any catalogs or streams.");
        }

        Entries.Add(entry);
        Attach(entry);
        Revision++;
        await RaiseChangedAsync(cancellationToken);
        return entry;
    }

    public async Task RemoveAsync(AddonEntry entry, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        if (Entries.Remove(entry))
        {
            entry.PropertyChanged -= OnEntryPropertyChanged;
            Revision++;
            await RaiseChangedAsync(cancellationToken);
        }
    }

    /// <summary>Moves an addon one row up (-1) or down (+1) and persists the order.</summary>
    public async Task<bool> MoveAsync(
        AddonEntry entry,
        int offset,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        var index = Entries.IndexOf(entry);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Entries.Count)
        {
            return false;
        }

        Entries.Move(index, target);
        Revision++;
        await RaiseChangedAsync(cancellationToken);
        return true;
    }

    /// <summary>Removes every entry and restores the bundled addons in their default order.</summary>
    public async Task RestoreDefaultsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        foreach (var entry in Entries)
        {
            entry.PropertyChanged -= OnEntryPropertyChanged;
        }

        Entries.Clear();

        foreach (var manifestUrl in DefaultManifestUrls)
        {
            var entry = new AddonEntry(manifestUrl);
            await entry.LoadManifestAsync(cancellationToken);
            Entries.Add(entry);
            Attach(entry);
        }

        Revision++;
        await RaiseChangedAsync(cancellationToken);
    }

    public static bool UrlEquals(string left, string right) =>
        left.TrimEnd('/').Equals(right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    public static bool IsDefaultUrl(string manifestUrl) =>
        DefaultManifestUrls.Any(url => UrlEquals(url, manifestUrl));

    private async Task LoadCoreAsync()
    {
        var path = GetStoragePath();
        List<AddonRecord> records;
        var shouldSave = false;

        try
        {
            if (File.Exists(path))
            {
                records = ParseStore(await File.ReadAllTextAsync(path));
            }
            else
            {
                records = DefaultManifestUrls
                    .Select(url => new AddonRecord { Url = url, Enabled = true })
                    .ToList();
                shouldSave = true;
            }
        }
        catch
        {
            records = DefaultManifestUrls
                .Select(url => new AddonRecord { Url = url, Enabled = true })
                .ToList();
            shouldSave = true;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (!TryNormalizeManifestUrl(record.Url, out var normalized) || !seen.Add(normalized))
            {
                continue;
            }

            var entry = new AddonEntry(normalized) { IsEnabled = record.Enabled };
            Entries.Add(entry);
            Attach(entry);
        }

        RefreshOrder();

        if (shouldSave)
        {
            await SaveAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Accepts both the current format ({ version, addons: [...] }) and the
    /// original bare array of manifest URLs.
    /// </summary>
    private static List<AddonRecord> ParseStore(string json)
    {
        var records = new List<AddonRecord>();
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    records.Add(new AddonRecord { Url = element.GetString() ?? "" });
                }
            }

            return records;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(document.RootElement, "addons", out var addons) ||
            addons.ValueKind != JsonValueKind.Array)
        {
            return records;
        }

        foreach (var element in addons.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                records.Add(new AddonRecord { Url = element.GetString() ?? "" });
                continue;
            }

            if (element.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(element, "url", out var url) ||
                url.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var enabled = !TryGetProperty(element, "enabled", out var flag) ||
                          flag.ValueKind != JsonValueKind.False;
            records.Add(new AddonRecord { Url = url.GetString() ?? "", Enabled = enabled });
        }

        return records;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private void Attach(AddonEntry entry) => entry.PropertyChanged += OnEntryPropertyChanged;

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AddonEntry.IsEnabled) || sender is not AddonEntry entry)
        {
            return;
        }

        Revision++;
        _ = RaiseChangedAsync(CancellationToken.None);

        if (entry.IsEnabled)
        {
            _ = entry.LoadManifestAsync();
        }
    }

    private async Task RaiseChangedAsync(CancellationToken cancellationToken)
    {
        RefreshOrder();
        Changed?.Invoke(this, EventArgs.Empty);
        await SaveAsync(cancellationToken);
    }

    private void RefreshOrder()
    {
        for (var index = 0; index < Entries.Count; index++)
        {
            var entry = Entries[index];
            entry.Order = index + 1;
            entry.CanMoveUp = index > 0;
            entry.CanMoveDown = index < Entries.Count - 1;
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
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

            var store = new AddonStore
            {
                Addons = Entries
                    .Select(entry => new AddonRecord
                    {
                        Url = entry.ManifestUrl,
                        Enabled = entry.IsEnabled,
                    })
                    .ToList(),
            };

            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(store, StoreJsonOptions),
                cancellationToken);
        }
        catch
        {
            // The in-memory list remains usable if persistence fails.
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

        _storagePath = Path.Combine(LocalPaths.Root, "addons.json");
        return _storagePath;
    }

    private static bool TryNormalizeManifestUrl(string? manifestUrl, out string normalized)
    {
        normalized = "";
        var value = manifestUrl?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        normalized = uri.ToString();
        return true;
    }

    private sealed class AddonStore
    {
        public int Version { get; set; } = 2;

        public List<AddonRecord> Addons { get; set; } = [];
    }

    private sealed class AddonRecord
    {
        public string Url { get; set; } = "";

        public bool Enabled { get; set; } = true;
    }
}

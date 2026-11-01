using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Pirsa.Models;
using Windows.Storage;

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

    public string Name => Manifest?.Name is { Length: > 0 } name ? name : Host;

    public string VersionText =>
        string.IsNullOrWhiteSpace(Manifest?.Version) ? "" : $"v{Manifest.Version}";

    public string DescriptionText => Manifest?.Description ?? "";

    public bool HasDescription => !string.IsNullOrWhiteSpace(DescriptionText);

    public bool IsReady => Manifest is not null;

    public bool HasError => !string.IsNullOrWhiteSpace(LoadError);

    public bool IsDefault => AddonRegistry.IsDefaultUrl(ManifestUrl);

    public bool HasCatalogs => Manifest?.Catalogs is { Count: > 0 };

    public bool HasStreams => Manifest?.Resources.Any(resource =>
        resource.Name.Equals("stream", StringComparison.OrdinalIgnoreCase)) == true;

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

            var names = Manifest.Resources
                .Select(resource => resource.Name.ToLowerInvariant())
                .Distinct()
                .ToList();

            var parts = new List<string>();
            if (Manifest.Catalogs.Count > 0 || names.Contains("catalog")) parts.Add("Catalogs");
            if (names.Contains("stream")) parts.Add("Streams");
            if (names.Contains("meta")) parts.Add("Metadata");
            if (names.Contains("subtitles")) parts.Add("Subtitles");

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
        OnPropertyChanged(nameof(TypesText));
        OnPropertyChanged(nameof(CapabilitiesText));
        OnPropertyChanged(nameof(HasError));
    }

    partial void OnLoadErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
    }

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

    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Task? _loadTask;
    private string? _storagePath;

    public ObservableCollection<AddonEntry> Entries { get; } = [];

    public int Revision { get; private set; }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        Task load;
        lock (_gate)
        {
            load = _loadTask ??= LoadCoreAsync();
        }

        await load.WaitAsync(cancellationToken);
    }

    /// <summary>Fetches manifests for all configured addons in parallel.</summary>
    public async Task LoadManifestsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        await Task.WhenAll(Entries.Select(entry => entry.LoadManifestAsync(cancellationToken)));
    }

    public AddonEntry? GetEntry(string? manifestUrl)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            return null;
        }

        return Entries.FirstOrDefault(entry => UrlEquals(entry.ManifestUrl, manifestUrl));
    }

    public StremioAddonClient? GetClient(string? manifestUrl) => GetEntry(manifestUrl)?.Client;

    /// <summary>Validates and adds a manifest URL, then persists the list.</summary>
    public async Task<AddonEntry> AddAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        if (!TryNormalizeManifestUrl(manifestUrl, out var normalized))
        {
            throw new StremioException("Enter a valid http(s) Stremio addon manifest URL.");
        }

        if (Entries.Any(entry => UrlEquals(entry.ManifestUrl, normalized)))
        {
            throw new StremioException("That addon is already configured.");
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
        Revision++;
        await SaveAsync(cancellationToken);
        return entry;
    }

    public async Task RemoveAsync(AddonEntry entry, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        if (Entries.Remove(entry))
        {
            Revision++;
            await SaveAsync(cancellationToken);
        }
    }

    /// <summary>Removes every entry and restores the bundled Cinemeta addon.</summary>
    public async Task RestoreDefaultsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        Entries.Clear();

        foreach (var manifestUrl in DefaultManifestUrls)
        {
            var entry = new AddonEntry(manifestUrl);
            await entry.LoadManifestAsync(cancellationToken);
            Entries.Add(entry);
        }

        Revision++;
        await SaveAsync(cancellationToken);
    }

    public static bool UrlEquals(string left, string right) =>
        left.TrimEnd('/').Equals(right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    public static bool IsDefaultUrl(string manifestUrl) =>
        DefaultManifestUrls.Any(url => UrlEquals(url, manifestUrl));

    private async Task LoadCoreAsync()
    {
        var path = GetStoragePath();
        List<string> urls;
        var shouldSave = false;

        try
        {
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path);
                urls = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            }
            else
            {
                urls = [.. DefaultManifestUrls];
                shouldSave = true;
            }
        }
        catch
        {
            urls = [.. DefaultManifestUrls];
            shouldSave = true;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in urls)
        {
            if (!TryNormalizeManifestUrl(raw, out var normalized) || !seen.Add(normalized))
            {
                continue;
            }

            Entries.Add(new AddonEntry(normalized));
        }

        if (shouldSave)
        {
            await SaveAsync(CancellationToken.None);
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

            var urls = Entries.Select(entry => entry.ManifestUrl).ToList();
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(urls, new JsonSerializerOptions { WriteIndented = true }),
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

        try
        {
            _storagePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "addons.json");
        }
        catch
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Pirsa");
            _storagePath = Path.Combine(directory, "addons.json");
        }

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
}

using System.Text.Json;
using Pirsa.Models;

namespace Pirsa.Services;

/// <summary>
/// Raised when an addon cannot be reached or returns an unusable response.
/// </summary>
public sealed class StremioException(string message) : Exception(message);

/// <summary>
/// A small client for the Stremio addon protocol:
/// manifest → catalogs → meta. Nothing here is Cinemeta-specific, so any
/// addon manifest URL can be plugged in.
/// </summary>
public sealed class StremioAddonClient
{
    /// <summary>Bundled default addon.</summary>
    public const string DefaultManifestUrl = "https://v3-cinemeta.strem.io/manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly Uri _manifestUri;
    private readonly SemaphoreSlim _manifestGate = new(1, 1);
    private Uri? _baseUri;
    private AddonManifest? _manifest;

    public StremioAddonClient(string manifestUrl)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("The addon manifest URL is not a valid absolute URL.", nameof(manifestUrl));
        }

        _manifestUri = uri;
    }

    public string ManifestUrl => _manifestUri.ToString();

    public AddonManifest? Manifest => _manifest;

    public string AddonName => _manifest?.Name is { Length: > 0 } name ? name : "Addon";

    public async Task<AddonManifest> GetManifestAsync(CancellationToken cancellationToken = default)
    {
        if (_manifest is not null)
        {
            return _manifest;
        }

        await _manifestGate.WaitAsync(cancellationToken);
        try
        {
            if (_manifest is not null)
            {
                return _manifest;
            }

            var manifest = await GetJsonAsync<AddonManifest>(_manifestUri, cancellationToken)
                ?? throw new StremioException($"{_manifestUri.Host} returned an empty manifest.");

            _manifest = manifest;
            _baseUri = new Uri(_manifestUri, ".");
            return manifest;
        }
        finally
        {
            _manifestGate.Release();
        }
    }

    /// <summary>
    /// Fetches one catalog page. <paramref name="genre"/> is used for catalogs
    /// such as Cinemeta's "New" rows that require a year value.
    /// </summary>
    public async Task<IReadOnlyList<MetaItem>> GetCatalogAsync(
        AddonCatalog catalog,
        string? genre = null,
        int skip = 0,
        CancellationToken cancellationToken = default)
    {
        var manifest = await GetManifestAsync(cancellationToken);
        if (!manifest.SupportsCatalog)
        {
            throw new StremioException($"{manifest.Name} doesn't provide catalogs.");
        }

        var extras = new List<string>();
        if (!string.IsNullOrWhiteSpace(genre))
        {
            extras.Add($"genre={Uri.EscapeDataString(genre)}");
        }

        if (skip > 0)
        {
            extras.Add($"skip={skip}");
        }

        var extraPath = extras.Count > 0 ? "/" + string.Join('&', extras) : "";
        var url = new Uri(BaseUri,
            $"catalog/{Uri.EscapeDataString(catalog.Type)}/{Uri.EscapeDataString(catalog.Id)}{extraPath}.json");

        var response = await GetJsonAsync<CatalogResponse>(url, cancellationToken)
            ?? throw new StremioException($"{manifest.Name} returned an empty catalog response.");

        return response.Metas ?? [];
    }

    /// <summary>
    /// Fetches playable sources for a movie or episode id. Stream-only addons
    /// such as PenguPlay return their links here.
    /// </summary>
    public async Task<IReadOnlyList<StreamItem>> GetStreamsAsync(
        string type,
        string id,
        CancellationToken cancellationToken = default)
    {
        var manifest = await GetManifestAsync(cancellationToken);
        if (!manifest.SupportsStream(type, id))
        {
            return [];
        }

        var url = new Uri(BaseUri,
            $"stream/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(id)}.json");

        var response = await GetJsonAsync<StreamResponse>(url, cancellationToken);
        return response?.Streams ?? [];
    }

    /// <summary>
    /// Fetches subtitle tracks for a movie or episode id. Subtitle addons such
    /// as OpenSubtitles v3 return { subtitles: [{ id, url, lang }] }.
    /// </summary>
    public async Task<IReadOnlyList<SubtitleItem>> GetSubtitlesAsync(
        string type,
        string id,
        CancellationToken cancellationToken = default)
    {
        var manifest = await GetManifestAsync(cancellationToken);
        if (!manifest.SupportsSubtitles(type, id))
        {
            return [];
        }

        var url = new Uri(BaseUri,
            $"subtitles/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(id)}.json");

        var response = await GetJsonAsync<SubtitleResponse>(url, cancellationToken);
        return response?.Subtitles ?? [];
    }

    /// <summary>Fetches full details (episodes, cast, artwork) for one item.</summary>
    public async Task<MetaItem> GetMetaAsync(
        string type,
        string id,
        CancellationToken cancellationToken = default)
    {
        var manifest = await GetManifestAsync(cancellationToken);
        var url = new Uri(BaseUri,
            $"meta/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(id)}.json");

        var response = await GetJsonAsync<MetaResponse>(url, cancellationToken)
            ?? throw new StremioException($"{manifest.Name} returned an empty meta response.");

        return response.Meta
            ?? throw new StremioException($"{manifest.Name} doesn't know this title.");
    }

    private Uri BaseUri =>
        _baseUri ?? throw new InvalidOperationException("The addon manifest has not been loaded yet.");

    private static async Task<T?> GetJsonAsync<T>(Uri url, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StremioException($"{url.Host} took too long to respond.");
        }
        catch (HttpRequestException)
        {
            throw new StremioException($"Couldn't reach {url.Host}. Check your connection and try again.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new StremioException(
                    $"{url.Host} returned an error ({(int)response.StatusCode}).");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            try
            {
                return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
            }
            catch (JsonException)
            {
                throw new StremioException($"{url.Host} returned data Pirsa couldn't read.");
            }
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Pirsa/1.0 (WinUI)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}

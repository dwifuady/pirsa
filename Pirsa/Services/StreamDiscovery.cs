using Pirsa.Models;

namespace Pirsa.Services;

/// <summary>
/// Raw stream results from one addon, before ranking and deduplication.
/// </summary>
public sealed record AddonStreamResult(
    string AddonName,
    string? ManifestUrl,
    IReadOnlyList<StreamItem> Streams,
    string? Error);

/// <summary>
/// Fetches streams for a movie or episode from every configured addon and
/// caches the result briefly.
///
/// The details page calls <see cref="Warm"/> as soon as a title is open, so by
/// the time Play is pressed the network work is usually done and the source
/// picker (or autoplay choice) appears immediately. The player awaits the same
/// shared task, so a click while the prefetch is still running simply joins it
/// instead of starting a second round of addon requests.
/// </summary>
public sealed class StreamDiscovery
{
    /// <summary>How long a successful result is reused before asking the addons again.</summary>
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(10);

    /// <summary>Empty or failed results are retried sooner.</summary>
    private static readonly TimeSpan EmptyFreshFor = TimeSpan.FromSeconds(60);

    private const int MaxEntries = 24;

    private readonly AddonRegistry _registry;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<IReadOnlyList<AddonStreamResult>>> _inFlight = new(StringComparer.Ordinal);

    public StreamDiscovery(AddonRegistry registry)
    {
        _registry = registry;

        // A changed addon list invalidates every cached result.
        _registry.Entries.CollectionChanged += (_, _) => Clear();
    }

    /// <summary>Starts a background fetch. Failures are ignored.</summary>
    public void Warm(PlaybackRequest request) => _ = WarmCoreAsync(request);

    public async Task<IReadOnlyList<AddonStreamResult>> GetAsync(
        PlaybackRequest request,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var key = CacheKey(request);
        Task<IReadOnlyList<AddonStreamResult>> fetch;

        lock (_gate)
        {
            if (!refresh &&
                _cache.TryGetValue(key, out var cached) &&
                cached.IsFresh)
            {
                return cached.Results;
            }

            if (!_inFlight.TryGetValue(key, out fetch!))
            {
                fetch = DiscoverAsync(request);
                _inFlight[key] = fetch;
                _ = StoreWhenFinishedAsync(key, fetch);
            }
        }

        return await fetch.WaitAsync(cancellationToken);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _cache.Clear();
            _inFlight.Clear();
        }
    }

    private async Task WarmCoreAsync(PlaybackRequest request)
    {
        try
        {
            await GetAsync(request);
        }
        catch
        {
            // Prefetching is invisible; the player retries on demand.
        }
    }

    private async Task StoreWhenFinishedAsync(
        string key,
        Task<IReadOnlyList<AddonStreamResult>> fetch)
    {
        try
        {
            var results = await fetch;
            var hasStreams = results.Any(result => result.Streams.Count > 0);

            lock (_gate)
            {
                _inFlight.Remove(key);
                _cache[key] = new CacheEntry(
                    results,
                    DateTimeOffset.UtcNow,
                    hasStreams ? FreshFor : EmptyFreshFor);

                while (_cache.Count > MaxEntries)
                {
                    var oldest = _cache.MinBy(pair => pair.Value.SavedAt);
                    _cache.Remove(oldest.Key);
                }
            }
        }
        catch
        {
            lock (_gate)
            {
                _inFlight.Remove(key);
            }
        }
    }

    private async Task<IReadOnlyList<AddonStreamResult>> DiscoverAsync(PlaybackRequest request)
    {
        try
        {
            await _registry.EnsureLoadedAsync();
        }
        catch
        {
            return [];
        }

        await _registry.LoadManifestsAsync();

        var streamAddons = _registry.Entries
            .Where(entry => entry.Manifest?.Resources.Any(resource =>
                resource.Name.Equals("stream", StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();

        // If no manifest declares streams, still ask every addon: some older
        // addons omit the resource list but answer stream requests anyway.
        var candidates = streamAddons.Count > 0 ? streamAddons : _registry.Entries.ToList();

        if (candidates.Count == 0)
        {
            return [];
        }

        var results = await Task.WhenAll(
            candidates.Select(entry => FetchAsync(entry, request)));

        return results;
    }

    private static async Task<AddonStreamResult> FetchAsync(
        AddonEntry entry,
        PlaybackRequest request)
    {
        try
        {
            var streams = await entry.Client.GetStreamsAsync(
                request.Type,
                request.VideoId);

            return new AddonStreamResult(
                entry.Name,
                entry.ManifestUrl,
                streams,
                null);
        }
        catch (Exception exception)
        {
            return new AddonStreamResult(
                entry.Name,
                entry.ManifestUrl,
                [],
                exception.Message);
        }
    }

    private static string CacheKey(PlaybackRequest request) =>
        $"{request.Type.ToLowerInvariant()}|{request.VideoId}";

    private sealed record CacheEntry(
        IReadOnlyList<AddonStreamResult> Results,
        DateTimeOffset SavedAt,
        TimeSpan Lifetime)
    {
        public bool IsFresh => DateTimeOffset.UtcNow - SavedAt < Lifetime;
    }
}

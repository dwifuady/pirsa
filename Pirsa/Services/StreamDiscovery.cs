using System.Threading.Channels;
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
/// One in-flight (or cached) discovery pass for a movie or episode. Results are
/// published as soon as each addon answers, so a slow addon never delays the
/// sources the others already returned. Several consumers (the details page
/// prefetch and the player) can read the same pass.
/// </summary>
public sealed class StreamLoadSession
{
    private readonly object _gate = new();
    private readonly List<AddonStreamResult> _buffered = [];
    private readonly List<StreamSubscription> _subscriptions = [];
    private readonly TaskCompletionSource<IReadOnlyList<AddonStreamResult>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;

    internal StreamLoadSession(int generation, IReadOnlyList<AddonStreamResult>? seeded = null)
    {
        Generation = generation;

        if (seeded is not null)
        {
            _buffered.AddRange(seeded);
            _completed = true;
            _completion.TrySetResult(_buffered.ToArray());
        }
    }

    internal int Generation { get; }

    /// <summary>Completes when every addon has answered, successfully or not.</summary>
    public Task<IReadOnlyList<AddonStreamResult>> Completion => _completion.Task;

    public IReadOnlyList<AddonStreamResult> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _buffered.ToArray();
            }
        }
    }

    /// <summary>Buffered results first, then every result as it arrives.</summary>
    public StreamSubscription Subscribe()
    {
        var subscription = new StreamSubscription(this);

        lock (_gate)
        {
            foreach (var result in _buffered)
            {
                subscription.TryWrite(result);
            }

            if (_completed)
            {
                subscription.TryComplete();
            }
            else
            {
                _subscriptions.Add(subscription);
            }
        }

        return subscription;
    }

    internal void Publish(AddonStreamResult result)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _buffered.Add(result);
            foreach (var subscription in _subscriptions)
            {
                subscription.TryWrite(result);
            }
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            foreach (var subscription in _subscriptions)
            {
                subscription.TryComplete();
            }

            _subscriptions.Clear();
            _completion.TrySetResult(_buffered.ToArray());
        }
    }

    internal void Remove(StreamSubscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }
}

/// <summary>
/// A single reader of a <see cref="StreamLoadSession"/>. Disposing it detaches
/// from the session without stopping the other readers.
/// </summary>
public sealed class StreamSubscription : IAsyncEnumerable<AddonStreamResult>, IAsyncDisposable
{
    private readonly StreamLoadSession _session;
    private readonly Channel<AddonStreamResult> _channel =
        Channel.CreateUnbounded<AddonStreamResult>(new UnboundedChannelOptions { SingleReader = true });
    private bool _disposed;

    internal StreamSubscription(StreamLoadSession session)
    {
        _session = session;
    }

    internal void TryWrite(AddonStreamResult result) => _channel.Writer.TryWrite(result);

    internal void TryComplete() => _channel.Writer.TryComplete();

    public async IAsyncEnumerator<AddonStreamResult> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var result in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return result;
            }
        }
        finally
        {
            _session.Remove(this);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            TryComplete();
            _session.Remove(this);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Fetches streams for a movie or episode from every enabled addon.
///
/// Addons are asked in the user's priority order, but the results are published
/// in arrival order and never wait for the slowest response. A pass is cached
/// briefly once it finishes, so the player can reuse the details page prefetch;
/// a changed addon list invalidates both the cache and any running pass.
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
    private readonly Dictionary<string, StreamLoadSession> _sessions = new(StringComparer.Ordinal);
    private int _generation;

    public StreamDiscovery(AddonRegistry registry)
    {
        _registry = registry;

        // A changed addon list invalidates every cached result.
        _registry.Changed += (_, _) => Invalidate();
    }

    /// <summary>Starts (or joins) a pass. Calling this from a prefetch is cheap.</summary>
    public StreamLoadSession Start(PlaybackRequest request, bool refresh = false)
    {
        var key = CacheKey(request);

        lock (_gate)
        {
            if (!refresh && _cache.TryGetValue(key, out var cached) && cached.IsFresh)
            {
                return new StreamLoadSession(_generation, cached.Results);
            }

            if (!refresh && _sessions.TryGetValue(key, out var running))
            {
                return running;
            }

            var session = new StreamLoadSession(_generation);
            _sessions[key] = session;
            _ = RunAsync(key, session, request);
            return session;
        }
    }

    /// <summary>Starts a background pass. Failures are ignored.</summary>
    public void Warm(PlaybackRequest request) => _ = Start(request).Completion;

    private async Task RunAsync(string key, StreamLoadSession session, PlaybackRequest request)
    {
        try
        {
            await _registry.EnsureLoadedAsync();
            await _registry.LoadManifestsAsync();

            var enabled = _registry.EnabledEntries;
            var streamAddons = enabled.Where(entry => entry.HasStreams).ToList();

            // If no manifest declares streams, still ask every enabled addon:
            // some older addons omit the resource list but answer stream
            // requests anyway.
            var candidates = streamAddons.Count > 0 ? streamAddons : enabled.ToList();

            // Starting the requests in list order gives the top addons the
            // first chance to answer; awaiting with WhenAny reports whichever
            // finished, so one slow addon never blocks the list.
            var pending = candidates.Select(entry => FetchAsync(entry, request)).ToList();
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending);
                pending.Remove(finished);
                session.Publish(await finished);
            }
        }
        catch
        {
            // Individual addon failures arrive as results with an error.
        }
        finally
        {
            session.Complete();
            Store(key, session);
        }
    }

    private void Store(string key, StreamLoadSession session)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(key, out var current) && ReferenceEquals(current, session))
            {
                _sessions.Remove(key);
            }

            // A pass started before an addon change must not repopulate the cache.
            if (session.Generation != _generation)
            {
                return;
            }

            var results = session.Snapshot;
            var hasStreams = results.Any(result => result.Streams.Count > 0);

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

    private void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _cache.Clear();

            // Running passes keep serving their existing readers, but new
            // reads start a fresh pass with the current addon list.
            _sessions.Clear();
        }
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

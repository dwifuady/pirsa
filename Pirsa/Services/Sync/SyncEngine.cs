using Pirsa.Services.Sync;

namespace Pirsa.Services;

/// <summary>
/// Keeps watched state and the watchlist in step with every connected provider.
/// Providers are independent mirrors: pull, merge by timestamp, then push the
/// merged local state. A failure in one never blocks another.
///
/// Sync never touches resume positions or remembered sources. Those stay on the
/// device that played the title.
/// </summary>
public sealed class SyncEngine
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(4);

    private readonly WatchProgressStore _watchProgress;
    private readonly WatchlistStore _watchlist;
    private readonly SyncStateStore _stateStore = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ISyncSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _debounce;
    private bool _started;
    private bool _applyingRemote;

    public SyncEngine(WatchProgressStore watchProgress, WatchlistStore watchlist)
    {
        _watchProgress = watchProgress;
        _watchlist = watchlist;
    }

    /// <summary>Raised after connections, sync times or errors change.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<SyncProviderDescriptor> Providers => SyncProviders.Descriptors;

    public IReadOnlyList<SyncConnection> Connections => _stateStore.State.Connections;

    public string SummaryText => Connections.Count switch
    {
        0 => "Not connected",
        1 => $"{NameFor(Connections[0].ProviderId)} connected",
        var count => $"{count} services connected",
    };

    public SyncConnection? FindConnection(string? providerId) =>
        Connections.FirstOrDefault(connection =>
            connection.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));

    public bool IsConnected(string? providerId) => FindConnection(providerId) is not null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _stateStore.EnsureLoadedAsync(cancellationToken);
        await _watchProgress.EnsureLoadedAsync(cancellationToken);
        await _watchlist.EnsureLoadedAsync(cancellationToken);

        if (_started)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _started = true;
        _watchProgress.Changed += Store_Changed;
        _watchlist.Changed += Store_Changed;
        _watchProgress.Unwatched += WatchProgress_Unwatched;
        _watchlist.Removed += Watchlist_Removed;

        Changed?.Invoke(this, EventArgs.Empty);

        await RestoreSessionsAsync(cancellationToken);

        if (Connections.Count > 0)
        {
            _ = SyncNowAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Validates the credentials, stores them encrypted, and runs a first
    /// pull/merge/push so a fresh connection is immediately in step.
    /// </summary>
    public async Task<SyncResult> ConnectAsync(
        string providerId,
        SyncCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var provider = SyncProviders.Find(providerId);
        if (provider is null)
        {
            return SyncResult.Fail("Unknown sync provider.");
        }

        ISyncSession session;
        try
        {
            session = await provider.ConnectAsync(credentials, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return SyncResult.Fail(exception.Message);
        }

        await _stateStore.EnsureLoadedAsync(cancellationToken);

        if (_sessions.Remove(providerId, out var previous))
        {
            await previous.DisposeAsync();
        }

        _sessions[providerId] = session;

        var connection = _stateStore.GetOrAddConnection(providerId);
        connection.AccountLabel = session.AccountLabel;
        connection.LastError = null;

        try
        {
            await _stateStore.SaveCredentialsAsync(providerId, credentials, cancellationToken);
            await _stateStore.SaveAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            _sessions.Remove(providerId);
            _stateStore.RemoveConnection(providerId);
            await session.DisposeAsync();
            return SyncResult.Fail($"Couldn't save the credentials. {exception.Message}");
        }

        Changed?.Invoke(this, EventArgs.Empty);

        // Go through the gate so a scheduled push cannot race this first pull.
        // Sync only this provider, so the message describes the one just added.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await SyncProviderAsync(connection, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Forgets the account on this device. Local history is deliberately kept,
    /// and nothing is deleted remotely.
    /// </summary>
    public async Task DisconnectAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        await _stateStore.EnsureLoadedAsync(cancellationToken);

        // Wait for an in-flight sync so its session is not disposed mid-request.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _stateStore.RemoveConnection(providerId);
            await _stateStore.RemoveCredentialsAsync(providerId, cancellationToken);

            if (_sessions.Remove(providerId, out var session))
            {
                await session.DisposeAsync();
            }

            await _stateStore.SaveAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<SyncResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        await _stateStore.EnsureLoadedAsync(cancellationToken);

        if (Connections.Count == 0)
        {
            return SyncResult.Fail("No sync provider is connected.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            SyncResult? last = null;
            foreach (var connection in Connections.ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                last = await SyncProviderAsync(connection, cancellationToken);
            }

            return last ?? SyncResult.Fail("Nothing to sync.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncResult> SyncProviderAsync(
        SyncConnection connection,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(connection.ProviderId, out var session))
        {
            connection.LastError = "Sign in again.";
            await SaveQuietlyAsync(cancellationToken);
            Changed?.Invoke(this, EventArgs.Empty);
            return SyncResult.Fail(connection.LastError);
        }

        try
        {
            var remote = await session.PullAsync(cancellationToken);
            var plan = SyncMerge.Plan(remote, BuildLocalSnapshot());

            if (plan.ApplyWatched.Count > 0 || plan.ApplyWatchlist.Count > 0)
            {
                await ApplyPlanAsync(plan, cancellationToken);
            }

            var merged = BuildLocalSnapshot();
            await session.PushAsync(remote, merged, cancellationToken);

            connection.LastSyncedAt = DateTimeOffset.UtcNow;
            connection.LastError = null;
            await SaveQuietlyAsync(cancellationToken);
            Changed?.Invoke(this, EventArgs.Empty);

            var total = merged.Watched.Count;
            return SyncResult.Ok(total == 1
                ? "Synced 1 watched title."
                : $"Synced {total} watched titles.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            connection.LastError = Describe(exception);
            await SaveQuietlyAsync(CancellationToken.None);
            Changed?.Invoke(this, EventArgs.Empty);
            return SyncResult.Fail(connection.LastError);
        }
    }

    private async Task ApplyPlanAsync(SyncPlan plan, CancellationToken cancellationToken)
    {
        _applyingRemote = true;
        try
        {
            foreach (var item in plan.ApplyWatched)
            {
                if (item.Change == WatchEventKind.Watched)
                {
                    await _watchProgress.ApplyRemoteWatchedAsync(
                        item.ImdbId,
                        item.Kind,
                        item.Season ?? 0,
                        item.Episode ?? 0,
                        item.At,
                        item.Name,
                        item.Poster,
                        item.Type,
                        cancellationToken);
                }
                else
                {
                    await _watchProgress.ApplyRemoteUnwatchedAsync(
                        item.ImdbId,
                        item.Kind,
                        item.Season ?? 0,
                        item.Episode ?? 0,
                        cancellationToken);
                }
            }

            foreach (var item in plan.ApplyWatchlist)
            {
                if (item.Added)
                {
                    await _watchlist.ApplyRemoteAsync(
                        item.ImdbId,
                        item.Type ?? (item.Kind == SyncMediaKind.Series ? "series" : "movie"),
                        item.Name,
                        item.Poster,
                        item.At,
                        cancellationToken);
                }
                else
                {
                    await _watchlist.ApplyRemoteRemoveAsync(item.ImdbId, cancellationToken);
                }
            }

            foreach (var tombstone in plan.AddUnwatched)
            {
                _stateStore.RecordUnwatched(tombstone);
            }

            foreach (var tombstone in plan.AddWatchlistRemoved)
            {
                _stateStore.RecordWatchlistRemoved(tombstone);
            }
        }
        finally
        {
            _applyingRemote = false;
        }
    }

    /// <summary>
    /// Everything this device would push: one newest fact per key, so a later
    /// rewatch wins over an earlier unwatch instead of sending both.
    /// </summary>
    private SyncSnapshot BuildLocalSnapshot()
    {
        var watched = new List<WatchEvent>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _watchProgress.Entries)
        {
            if (!entry.IsWatched)
            {
                continue;
            }

            var imdbId = entry.ImdbId ?? SyncKey.ImdbFrom(entry.MetaId);
            if (string.IsNullOrWhiteSpace(imdbId))
            {
                continue;
            }

            var kind = entry.IsSeries ? SyncMediaKind.Series : SyncMediaKind.Movie;
            if (kind == SyncMediaKind.Series && (entry.Season <= 0 || entry.Episode <= 0))
            {
                continue;
            }

            var item = new WatchEvent(
                imdbId,
                kind,
                kind == SyncMediaKind.Series ? entry.Season : null,
                kind == SyncMediaKind.Series ? entry.Episode : null,
                WatchEventKind.Watched,
                entry.WatchedAt ?? entry.UpdatedAt,
                entry.Name,
                entry.Poster,
                entry.Type);

            if (seen.Add(item.Key))
            {
                watched.Add(item);
            }
        }

        var watchedKeys = watched.ToDictionary(
            item => item.Key,
            item => item.At,
            StringComparer.OrdinalIgnoreCase);

        var unwatched = _stateStore.State.Unwatched
            .Where(item => !watchedKeys.TryGetValue(item.Key, out var at) || at < item.At)
            .ToList();

        var watchlist = new List<WatchlistChange>();
        var listSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _watchlist.Entries)
        {
            var imdbId = SyncKey.ImdbFrom(entry.MetaId);
            if (string.IsNullOrWhiteSpace(imdbId))
            {
                continue;
            }

            var kind = IsSeriesType(entry.Type)
                ? SyncMediaKind.Series
                : SyncMediaKind.Movie;

            var item = new WatchlistChange(
                imdbId,
                kind,
                true,
                entry.AddedAt,
                entry.Name,
                entry.Poster,
                entry.Type);

            if (listSeen.Add(item.Key))
            {
                watchlist.Add(item);
            }
        }

        var listKeys = watchlist.ToDictionary(
            item => item.Key,
            item => item.At,
            StringComparer.OrdinalIgnoreCase);

        var listRemoved = _stateStore.State.WatchlistRemoved
            .Where(item => !listKeys.TryGetValue(item.Key, out var at) || at < item.At)
            .ToList();

        return new SyncSnapshot(watched, unwatched, watchlist, listRemoved);
    }

    private async Task RestoreSessionsAsync(CancellationToken cancellationToken)
    {
        foreach (var connection in Connections.ToList())
        {
            var provider = SyncProviders.Find(connection.ProviderId);
            if (provider is null)
            {
                connection.LastError = "This provider is no longer available.";
                continue;
            }

            try
            {
                var credentials = await _stateStore.LoadCredentialsAsync(
                    connection.ProviderId,
                    cancellationToken);

                if (credentials is null)
                {
                    connection.LastError = "Sign in again.";
                    continue;
                }

                var session = await provider.ConnectAsync(credentials, cancellationToken);
                _sessions[connection.ProviderId] = session;
                connection.AccountLabel = session.AccountLabel;
                connection.LastError = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                connection.LastError = Describe(exception);
            }
        }

        await SaveQuietlyAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Store_Changed(object? sender, EventArgs e)
    {
        if (_applyingRemote)
        {
            return;
        }

        ScheduleSync();
    }

    private void WatchProgress_Unwatched(object? sender, WatchProgressEntry entry)
    {
        if (_applyingRemote)
        {
            return;
        }

        var imdbId = entry.ImdbId ?? SyncKey.ImdbFrom(entry.MetaId);
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return;
        }

        _stateStore.RecordUnwatched(new WatchEvent(
            imdbId,
            entry.IsSeries ? SyncMediaKind.Series : SyncMediaKind.Movie,
            entry.IsSeries ? entry.Season : null,
            entry.IsSeries ? entry.Episode : null,
            WatchEventKind.Unwatched,
            DateTimeOffset.UtcNow));

        _ = _stateStore.SaveAsync();
        ScheduleSync();
    }

    private void Watchlist_Removed(object? sender, WatchlistEntry entry)
    {
        if (_applyingRemote)
        {
            return;
        }

        var imdbId = SyncKey.ImdbFrom(entry.MetaId);
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return;
        }

        _stateStore.RecordWatchlistRemoved(new WatchlistChange(
            imdbId,
            IsSeriesType(entry.Type) ? SyncMediaKind.Series : SyncMediaKind.Movie,
            false,
            DateTimeOffset.UtcNow));

        _ = _stateStore.SaveAsync();
        ScheduleSync();
    }

    private void ScheduleSync()
    {
        if (!_started || Connections.Count == 0 || _applyingRemote)
        {
            return;
        }

        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _debounce, next);
        previous?.Cancel();
        previous?.Dispose();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceDelay, next.Token);
                await SyncNowAsync(next.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // The provider row shows the error; a background push never nags.
            }
        });
    }

    private async Task SaveQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _stateStore.SaveAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private static bool IsSeriesType(string? type) =>
        type is not null &&
        (type.Equals("series", StringComparison.OrdinalIgnoreCase) ||
         type.Equals("tv", StringComparison.OrdinalIgnoreCase));

    private string NameFor(string providerId) =>
        SyncProviders.Find(providerId)?.Descriptor.Name ?? providerId;

    private static string Describe(Exception exception) => exception switch
    {
        StremioException => exception.Message,
        TaskCanceledException => "The server took too long to answer.",
        HttpRequestException => "Couldn't reach the server.",
        _ => exception.Message,
    };
}

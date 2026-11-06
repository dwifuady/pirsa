namespace Pirsa.Services.Sync;

/// <summary>
/// The timestamp comparison behind sync. Pure and I/O-free: the engine applies
/// what this returns, and a provider never has to know the merge rules.
/// </summary>
public static class SyncMerge
{
    /// <summary>
    /// Returns only the remote facts that are strictly newer than the local
    /// fact for the same key. A missing local row is older than anything.
    /// </summary>
    public static SyncPlan Plan(RemoteSnapshot remote, SyncSnapshot local)
    {
        var watchFacts = LocalWatchFacts(local);
        var listFacts = LocalWatchlistFacts(local);

        var applyWatched = new List<WatchEvent>();
        var addUnwatched = new List<WatchEvent>();

        foreach (var item in NewestPerKey(remote.Watched))
        {
            if (watchFacts.TryGetValue(item.Key, out var localFact) &&
                localFact.At >= item.At)
            {
                continue;
            }

            applyWatched.Add(item);

            if (item.Change == WatchEventKind.Unwatched)
            {
                addUnwatched.Add(item);
            }
        }

        var applyWatchlist = new List<WatchlistChange>();
        var addWatchlistRemoved = new List<WatchlistChange>();

        foreach (var item in NewestPerKey(remote.Watchlist))
        {
            if (listFacts.TryGetValue(item.Key, out var localFact) &&
                localFact.At >= item.At)
            {
                continue;
            }

            applyWatchlist.Add(item);

            if (!item.Added)
            {
                addWatchlistRemoved.Add(item);
            }
        }

        return new SyncPlan(
            applyWatched,
            applyWatchlist,
            addUnwatched,
            addWatchlistRemoved);
    }

    /// <summary>
    /// A provider may return the same key more than once when both a watched
    /// and an unwatched fact exist. Only the newest one can be applied.
    /// </summary>
    private static IReadOnlyList<WatchEvent> NewestPerKey(
        IReadOnlyList<WatchEvent> events)
    {
        var newest = new Dictionary<string, WatchEvent>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in events)
        {
            if (!newest.TryGetValue(item.Key, out var existing) || item.At > existing.At)
            {
                newest[item.Key] = item;
            }
        }

        return [.. newest.Values.OrderBy(item => item.At)];
    }

    private static IReadOnlyList<WatchlistChange> NewestPerKey(
        IReadOnlyList<WatchlistChange> changes)
    {
        var newest = new Dictionary<string, WatchlistChange>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in changes)
        {
            if (!newest.TryGetValue(item.Key, out var existing) || item.At > existing.At)
            {
                newest[item.Key] = item;
            }
        }

        return [.. newest.Values.OrderBy(item => item.At)];
    }

    private static Dictionary<string, (WatchEventKind Change, DateTimeOffset At)> LocalWatchFacts(
        SyncSnapshot local)
    {
        var facts = new Dictionary<string, (WatchEventKind Change, DateTimeOffset At)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in local.Watched)
        {
            facts[item.Key] = (WatchEventKind.Watched, item.At);
        }

        foreach (var item in local.Unwatched)
        {
            if (!facts.TryGetValue(item.Key, out var existing) || item.At >= existing.At)
            {
                facts[item.Key] = (WatchEventKind.Unwatched, item.At);
            }
        }

        return facts;
    }

    private static Dictionary<string, (bool Added, DateTimeOffset At)> LocalWatchlistFacts(
        SyncSnapshot local)
    {
        var facts = new Dictionary<string, (bool Added, DateTimeOffset At)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in local.Watchlist)
        {
            facts[item.Key] = (true, item.At);
        }

        foreach (var item in local.WatchlistRemoved)
        {
            if (!facts.TryGetValue(item.Key, out var existing) || item.At >= existing.At)
            {
                facts[item.Key] = (false, item.At);
            }
        }

        return facts;
    }
}

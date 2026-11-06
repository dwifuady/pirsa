using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pirsa.Helpers;

namespace Pirsa.Services.Sync.Providers;

/// <summary>
/// MDBList's per-user API key. Like Simkl it stores watched state, a watchlist,
/// paused playback and scrobble sessions; unlike Simkl the key is issued to the
/// user, so there is no app registration and nothing to ship.
/// </summary>
public sealed class MdbListSyncProvider : ISyncProvider
{
    internal static Uri ApiBaseUri { get; set; } = new("https://api.mdblist.com");

    public SyncProviderDescriptor Descriptor { get; } = new(
        Id: "mdblist",
        Name: "MDBList",
        Description: "Keep watched history, your watchlist and paused playback in step with MDBList.",
        Coverage: "Movies · Series",
        Capabilities: SyncCapabilities.Watched |
                      SyncCapabilities.Watchlist |
                      SyncCapabilities.Resume |
                      SyncCapabilities.Scrobble,
        AuthKind: SyncAuthKind.ApiKey,
        ConnectLabel: "Connect MDBList",
        Placeholder: "Paste your MDBList API key",
        Hint: "Create a key at mdblist.com/preferences. It is stored encrypted on this device and is never shown again.");

    public async Task<ISyncSession> ConnectAsync(
        SyncCredentials credentials,
        IProgress<SyncAuthPrompt>? prompt = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            throw new StremioException("Paste your MDBList API key first.");
        }

        var session = new MdbListSyncSession(ApiBaseUri, credentials.ApiKey.Trim());
        await session.LoadAccountAsync(cancellationToken);
        return session;
    }
}

internal sealed class MdbListSyncSession : ISyncSession
{
    private const int PageSize = 1000;
    private const int PushBatchSize = 100;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly Uri _baseUri;
    private readonly string _apiKey;

    private MdbListCursor? _cursor;

    public MdbListSyncSession(Uri baseUri, string apiKey)
    {
        _baseUri = baseUri;
        _apiKey = apiKey;
    }

    public string AccountLabel { get; private set; } = "MDBList";

    public string? Cursor
    {
        get => _cursor is null
            ? null
            : JsonSerializer.Serialize(_cursor, JsonOptions);
        set => _cursor = string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<MdbListCursor>(value, JsonOptions);
    }

    public SyncCredentials? UpdatedCredentials => null;

    public async Task LoadAccountAsync(CancellationToken cancellationToken)
    {
        var (status, body) = await SendAsync(
            HttpMethod.Get,
            "/user",
            new Dictionary<string, string>(),
            content: null,
            cancellationToken);

        if (!IsSuccess(status))
        {
            throw new StremioException(DescribeFailure(status, body, "load the MDBList account"));
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            var name = ReadString(document.RootElement, "username")
                ?? ReadString(document.RootElement, "name");

            if (!string.IsNullOrWhiteSpace(name))
            {
                AccountLabel = name;
            }
        }
        catch (JsonException)
        {
            // Cosmetic only.
        }
    }

    // ------------------------------------------------------------------ pull

    public async Task<RemoteSnapshot> PullAsync(CancellationToken cancellationToken = default)
    {
        var watched = new List<WatchEvent>();
        var watchlist = new List<WatchlistChange>();

        await PullWatchedAsync(watched, cancellationToken);
        await PullWatchlistAsync(watchlist, cancellationToken);
        var resume = await PullPlaybackAsync(cancellationToken);

        return new RemoteSnapshot(watched, watchlist, resume);
    }

    private async Task PullWatchedAsync(
        List<WatchEvent> watched,
        CancellationToken cancellationToken)
    {
        string? cursor = null;

        for (var page = 0; page < 20; page++)
        {
            var query = new Dictionary<string, string> { ["limit"] = PageSize.ToString(CultureInfo.InvariantCulture) };
            if (cursor is not null)
            {
                query["cursor"] = cursor;
            }

            var (status, body) = await SendAsync(
                HttpMethod.Get,
                "/sync/watched",
                query,
                content: null,
                cancellationToken);

            if (!IsSuccess(status))
            {
                throw new StremioException(DescribeFailure(status, body, "read the MDBList history"));
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                return;
            }

            using (document)
            {
                var root = document.RootElement;

                if (root.TryGetProperty("movies", out var movies) &&
                    movies.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in movies.EnumerateArray())
                    {
                        ParseWatchedMovie(item, watched);
                    }
                }

                if (root.TryGetProperty("episodes", out var episodes) &&
                    episodes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in episodes.EnumerateArray())
                    {
                        ParseWatchedEpisode(item, watched);
                    }
                }

                cursor = ReadNextCursor(root);
            }

            if (cursor is null)
            {
                return;
            }
        }
    }

    private static void ParseWatchedMovie(JsonElement item, List<WatchEvent> watched)
    {
        if (!item.TryGetProperty("movie", out var movie) ||
            !TryReadImdb(movie, out var imdbId) ||
            ReadDate(item, "watched_at") is not { } at)
        {
            return;
        }

        watched.Add(new WatchEvent(
            imdbId,
            SyncMediaKind.Movie,
            null,
            null,
            WatchEventKind.Watched,
            at,
            ReadString(movie, "title"),
            null,
            "movie"));
    }

    private static void ParseWatchedEpisode(JsonElement item, List<WatchEvent> watched)
    {
        if (!item.TryGetProperty("episode", out var episode) ||
            !episode.TryGetProperty("show", out var show) ||
            !TryReadImdb(show, out var imdbId) ||
            ReadInt(episode, "season") is not { } season ||
            ReadInt(episode, "number") is not { } number ||
            (ReadDate(item, "last_watched_at") ?? ReadDate(item, "watched_at")) is not { } at)
        {
            return;
        }

        watched.Add(new WatchEvent(
            imdbId,
            SyncMediaKind.Series,
            season,
            number,
            WatchEventKind.Watched,
            at,
            ReadString(show, "title"),
            null,
            "series"));
    }

    private async Task PullWatchlistAsync(
        List<WatchlistChange> watchlist,
        CancellationToken cancellationToken)
    {
        string? cursor = null;

        for (var page = 0; page < 20; page++)
        {
            var query = new Dictionary<string, string> { ["limit"] = PageSize.ToString(CultureInfo.InvariantCulture) };
            if (cursor is not null)
            {
                query["cursor"] = cursor;
            }

            var (status, body) = await SendAsync(
                HttpMethod.Get,
                "/watchlist/items",
                query,
                content: null,
                cancellationToken);

            if (!IsSuccess(status))
            {
                throw new StremioException(DescribeFailure(status, body, "read the MDBList watchlist"));
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                return;
            }

            using (document)
            {
                var root = document.RootElement;

                if (root.TryGetProperty("movies", out var movies) &&
                    movies.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in movies.EnumerateArray())
                    {
                        ParseWatchlistItem(item, SyncMediaKind.Movie, "movie", watchlist);
                    }
                }

                if (root.TryGetProperty("shows", out var shows) &&
                    shows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in shows.EnumerateArray())
                    {
                        ParseWatchlistItem(item, SyncMediaKind.Series, "series", watchlist);
                    }
                }

                cursor = ReadNextCursor(root);
            }

            if (cursor is null)
            {
                return;
            }
        }
    }

    private static void ParseWatchlistItem(
        JsonElement item,
        SyncMediaKind kind,
        string type,
        List<WatchlistChange> watchlist)
    {
        var imdbId = ReadString(item, "imdb_id");

        if (string.IsNullOrWhiteSpace(imdbId) &&
            item.TryGetProperty("ids", out var ids))
        {
            imdbId = ReadString(ids, "imdb");
        }

        if (string.IsNullOrWhiteSpace(imdbId) ||
            !imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        watchlist.Add(new WatchlistChange(
            imdbId,
            kind,
            true,
            default,
            ReadString(item, "title"),
            null,
            type));
    }

    private async Task<IReadOnlyList<RemoteResume>> PullPlaybackAsync(
        CancellationToken cancellationToken)
    {
        var (status, body) = await SendAsync(
            HttpMethod.Get,
            "/sync/playback",
            new Dictionary<string, string>(),
            content: null,
            cancellationToken);

        if (!IsSuccess(status))
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return [];
        }

        var resume = new List<RemoteResume>();

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return resume;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                var entry = ParsePlayback(item);
                if (entry is not null)
                {
                    resume.Add(entry);
                }
            }
        }

        return resume;
    }

    private static RemoteResume? ParsePlayback(JsonElement item)
    {
        var runtime = ReadInt(item, "runtime");
        if (runtime is not > 0)
        {
            return null;
        }

        var progress = ReadDouble(item, "progress");
        var pausedAt = ReadDate(item, "paused_at") ?? ReadDate(item, "updated_at");

        if (progress is not > 0 || pausedAt is not { } at)
        {
            return null;
        }

        var type = ReadString(item, "type");
        var isMovie = string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase);

        if (isMovie)
        {
            if (!item.TryGetProperty("movie", out var movie) ||
                !TryReadImdb(movie, out var movieImdb))
            {
                return null;
            }

            return new RemoteResume(
                movieImdb,
                SyncMediaKind.Movie,
                null,
                null,
                progress.Value,
                runtime.Value * 60_000L,
                at,
                ReadString(movie, "title"),
                null,
                "movie",
                null,
                ReadLong(item, "id") is var id and > 0 ? id : null);
        }

        if (!item.TryGetProperty("show", out var show) ||
            !TryReadImdb(show, out var imdbId) ||
            !item.TryGetProperty("episode", out var episode) ||
            ReadInt(episode, "season") is not { } season ||
            ReadInt(episode, "number") is not { } number)
        {
            return null;
        }

        return new RemoteResume(
            imdbId,
            SyncMediaKind.Series,
            season,
            number,
            progress.Value,
            runtime.Value * 60_000L,
            at,
            ReadString(show, "title"),
            null,
            "series",
            ReadString(episode, "name"),
            ReadLong(item, "id") is var playbackId and > 0 ? playbackId : null);
    }

    // ------------------------------------------------------------------ push

    public async Task PushAsync(
        RemoteSnapshot remote,
        SyncSnapshot local,
        CancellationToken cancellationToken = default)
    {
        var threshold = _cursor?.PushedThrough;
        var pushedThrough = threshold ?? DateTimeOffset.MinValue;

        List<WatchEvent> newWatched;
        List<WatchlistChange> newWatchlist;

        if (threshold is { } since)
        {
            newWatched = local.Watched.Where(item => item.At >= since).ToList();
            newWatchlist = local.Watchlist.Where(item => item.At >= since).ToList();
        }
        else
        {
            var remoteWatched = Newest(remote.Watched);
            var remoteWatchlistKeys = remote.Watchlist
                .Select(item => item.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            newWatched = local.Watched
                .Where(item => !remoteWatched.TryGetValue(item.Key, out var at) || item.At > at)
                .ToList();

            // MDBList's watchlist has no timestamp, so presence is the only
            // reliable signal. Do not echo back what was just pulled.
            newWatchlist = local.Watchlist
                .Where(item => !remoteWatchlistKeys.Contains(item.Key))
                .ToList();
        }

        await PushWatchedAsync(newWatched, cancellationToken);
        await PushWatchlistAsync(
            newWatchlist,
            local.Watched,
            local.Unwatched,
            local.WatchlistRemoved,
            cancellationToken);

        foreach (var item in newWatched)
        {
            if (item.At > pushedThrough)
            {
                pushedThrough = item.At;
            }
        }

        foreach (var item in newWatchlist)
        {
            if (item.At > pushedThrough)
            {
                pushedThrough = item.At;
            }
        }

        if (threshold is null)
        {
            foreach (var item in local.Watched)
            {
                if (item.At > pushedThrough)
                {
                    pushedThrough = item.At;
                }
            }

            foreach (var item in local.Watchlist)
            {
                if (item.At > pushedThrough)
                {
                    pushedThrough = item.At;
                }
            }
        }

        _cursor = new MdbListCursor
        {
            PushedThrough = pushedThrough == DateTimeOffset.MinValue
                ? _cursor?.PushedThrough
                : pushedThrough,
        };
    }

    private async Task PushWatchedAsync(
        IReadOnlyList<WatchEvent> watched,
        CancellationToken cancellationToken)
    {
        if (watched.Count == 0)
        {
            return;
        }

        var movies = watched
            .Where(item => item.Kind == SyncMediaKind.Movie)
            .GroupBy(item => item.ImdbId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new WatchedMovie
            {
                Ids = new ImdbIds(group.Key),
                WatchedAt = FormatDate(group.Max(item => item.At)),
            })
            .ToList();

        var shows = watched
            .Where(item => item.Kind == SyncMediaKind.Series &&
                           item.Season is > 0 &&
                           item.Episode is > 0)
            .GroupBy(item => item.ImdbId, StringComparer.OrdinalIgnoreCase)
            .Select(showGroup => new WatchedShow
            {
                Ids = new ImdbIds(showGroup.Key),
                Seasons = [.. showGroup
                    .GroupBy(item => item.Season!.Value)
                    .OrderBy(season => season.Key)
                    .Select(season => new WatchedSeason
                    {
                        Number = season.Key,
                        WatchedAt = FormatDate(season.Max(item => item.At)),
                        Episodes = [.. season
                            .OrderBy(item => item.Episode)
                            .Select(item => new WatchedEpisode
                            {
                                Number = item.Episode!.Value,
                                WatchedAt = FormatDate(item.At),
                            })],
                    })],
            })
            .ToList();

        foreach (var batch in Batch(movies, shows))
        {
            var (status, body) = await SendAsync(
                HttpMethod.Post,
                "/sync/watched",
                new Dictionary<string, string>(),
                content: () => JsonContent(new { movies = batch.Movies, shows = batch.Shows }),
                cancellationToken);

            if (!IsSuccess(status))
            {
                throw new StremioException(DescribeFailure(status, body, "save watched state on MDBList"));
            }

            await WriteDelayAsync(cancellationToken);
        }
    }

    private async Task PushWatchlistAsync(
        IReadOnlyList<WatchlistChange> add,
        IReadOnlyList<WatchEvent> localWatched,
        IReadOnlyList<WatchEvent> unwatched,
        IReadOnlyList<WatchlistChange> removed,
        CancellationToken cancellationToken)
    {
        if (add.Count > 0)
        {
            var movies = add
                .Where(item => item.Kind == SyncMediaKind.Movie)
                .Select(item => new ListItem(new ImdbIds(item.ImdbId)))
                .GroupBy(item => item.Ids.Imdb, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            var shows = add
                .Where(item => item.Kind == SyncMediaKind.Series)
                .Select(item => new ListItem(new ImdbIds(item.ImdbId)))
                .GroupBy(item => item.Ids.Imdb, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            foreach (var batch in Batch(movies, shows))
            {
                var (status, body) = await SendAsync(
                    HttpMethod.Post,
                    "/watchlist/items/add",
                    new Dictionary<string, string>(),
                    content: () => JsonContent(new { movies = batch.Movies, shows = batch.Shows }),
                    cancellationToken);

                if (!IsSuccess(status))
                {
                    throw new StremioException(DescribeFailure(status, body, "save the MDBList watchlist"));
                }

                await WriteDelayAsync(cancellationToken);
            }
        }

        var watchedMovies = localWatched
            .Where(item => item.Kind == SyncMediaKind.Movie)
            .Select(item => item.ImdbId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var watchedShows = localWatched
            .Where(item => item.Kind == SyncMediaKind.Series)
            .Select(item => item.ImdbId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removeMovies = removed
            .Where(item => item.Kind == SyncMediaKind.Movie && !watchedMovies.Contains(item.ImdbId))
            .Select(item => new ListItem(new ImdbIds(item.ImdbId)))
            .GroupBy(item => item.Ids.Imdb, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var removeShows = removed
            .Where(item => item.Kind == SyncMediaKind.Series && !watchedShows.Contains(item.ImdbId))
            .Select(item => new ListItem(new ImdbIds(item.ImdbId)))
            .GroupBy(item => item.Ids.Imdb, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (removeMovies.Count > 0 || removeShows.Count > 0)
        {
            foreach (var batch in Batch(removeMovies, removeShows))
            {
                var (status, body) = await SendAsync(
                    HttpMethod.Post,
                    "/watchlist/items/remove",
                    new Dictionary<string, string>(),
                    content: () => JsonContent(new { movies = batch.Movies, shows = batch.Shows }),
                    cancellationToken);

                if (!IsSuccess(status))
                {
                    throw new StremioException(DescribeFailure(status, body, "update the MDBList watchlist"));
                }

                await WriteDelayAsync(cancellationToken);
            }
        }

        await PushWatchedRemovalsAsync(unwatched, cancellationToken);
    }

    private async Task PushWatchedRemovalsAsync(
        IReadOnlyList<WatchEvent> unwatched,
        CancellationToken cancellationToken)
    {
        var movieRemovals = unwatched
            .Where(item => item.Kind == SyncMediaKind.Movie)
            .Select(item => new RemoveMovie(new ImdbIds(item.ImdbId)))
            .GroupBy(item => item.Ids.Imdb, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var episodeRemovals = unwatched
            .Where(item => item.Kind == SyncMediaKind.Series &&
                           item.Season is > 0 &&
                           item.Episode is > 0)
            .GroupBy(item => item.ImdbId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RemoveShow
            {
                Ids = new ImdbIds(group.Key),
                Seasons = [.. group
                    .GroupBy(item => item.Season!.Value)
                    .Select(season => new RemoveSeason
                    {
                        Number = season.Key,
                        Episodes = [.. season
                            .OrderBy(item => item.Episode)
                            .Select(item => new RemoveEpisode(item.Episode!.Value))],
                    })],
            })
            .ToList();

        var showRemovals = episodeRemovals
            .GroupBy(item => item.Ids.Imdb, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (movieRemovals.Count == 0 && showRemovals.Count == 0)
        {
            return;
        }

        foreach (var batch in Batch(movieRemovals, showRemovals))
        {
            var (status, body) = await SendAsync(
                HttpMethod.Post,
                "/sync/watched/remove",
                new Dictionary<string, string>(),
                content: () => JsonContent(new { movies = batch.Movies, shows = batch.Shows }),
                cancellationToken);

            if (!IsSuccess(status))
            {
                throw new StremioException(DescribeFailure(status, body, "remove watched state on MDBList"));
            }

            await WriteDelayAsync(cancellationToken);
        }
    }

    // ---------------------------------------------------------------- scrobble

    public async Task ScrobbleAsync(
        PlaybackSignal signal,
        PlaybackSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var action = signal switch
        {
            PlaybackSignal.Started => "start",
            PlaybackSignal.Paused => "pause",
            _ => "stop",
        };

        var progress = Math.Round(
            Math.Clamp(snapshot.Progress, 0, 100),
            2,
            MidpointRounding.AwayFromZero);

        object body = snapshot.Kind == SyncMediaKind.Series &&
                      snapshot.Season is > 0 &&
                      snapshot.Episode is > 0
            ? new
            {
                progress,
                show = new
                {
                    ids = new { imdb = snapshot.ImdbId },
                    season = snapshot.Season!.Value,
                    episode = snapshot.Episode!.Value,
                },
                app_name = "pirsa",
                app_version = AppVersion,
            }
            : new
            {
                progress,
                movie = new { ids = new { imdb = snapshot.ImdbId } },
                app_name = "pirsa",
                app_version = AppVersion,
            };

        var (status, responseBody) = await SendAsync(
            HttpMethod.Post,
            $"/scrobble/{action}",
            new Dictionary<string, string>(),
            content: () => JsonContent(body),
            cancellationToken);

        if (!IsSuccess(status))
        {
            throw new StremioException(DescribeFailure(status, responseBody, $"report a playback {action}"));
        }
    }

    public async Task RemovePlaybackAsync(
        PlaybackRef playback,
        CancellationToken cancellationToken = default)
    {
        object body = playback.Kind == SyncMediaKind.Series &&
                      playback.Season is > 0 &&
                      playback.Episode is > 0
            ? new
            {
                show = new
                {
                    ids = new { imdb = playback.ImdbId },
                    season = playback.Season!.Value,
                    episode = playback.Episode!.Value,
                },
            }
            : new
            {
                movie = new { ids = new { imdb = playback.ImdbId } },
            };

        var (status, _) = await SendAsync(
            HttpMethod.Post,
            "/scrobble/clear",
            new Dictionary<string, string>(),
            content: () => JsonContent(body),
            cancellationToken);

        if (!IsSuccess(status) && status != HttpStatusCode.NotFound)
        {
            throw new StremioException($"MDBList couldn't clear the playback ({status}).");
        }
    }

    // -------------------------------------------------------------------- http

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string> query,
        Func<HttpContent?>? content,
        CancellationToken cancellationToken)
    {
        var response = await SendOnceAsync(method, path, query, content, cancellationToken);

        if (response.Status == HttpStatusCode.TooManyRequests &&
            response.RetryAfter is { } retryAfter &&
            retryAfter <= TimeSpan.FromSeconds(30))
        {
            await Task.Delay(retryAfter, cancellationToken);
            response = await SendOnceAsync(method, path, query, content, cancellationToken);
        }

        return (response.Status, response.Body);
    }

    private async Task<(HttpStatusCode Status, string Body, TimeSpan? RetryAfter)> SendOnceAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string> query,
        Func<HttpContent?>? content,
        CancellationToken cancellationToken)
    {
        var uri = BuildUri(path, query);

        using var request = new HttpRequestMessage(method, uri);

        if (content is not null)
        {
            request.Content = content();
        }

        request.Headers.UserAgent.ParseAdd($"pirsa/{AppVersion}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response.StatusCode, body, response.Headers.RetryAfter?.Delta);
    }

    private Uri BuildUri(string path, IReadOnlyDictionary<string, string> query)
    {
        var parameters = new List<string>
        {
            $"apikey={Uri.EscapeDataString(_apiKey)}",
        };

        foreach (var pair in query)
        {
            parameters.Add($"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");
        }

        return new Uri(_baseUri, $"{path}?{string.Join("&", parameters)}");
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    // ----------------------------------------------------------------- helpers

    private static string AppVersion => AppInfo.HasVersion ? AppInfo.Version : "1.0";

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    private static HttpContent JsonContent(object body) =>
        new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json");

    private static async Task WriteDelayAsync(CancellationToken cancellationToken)
    {
        // MDBList allows 300 writes per five minutes; batching already keeps
        // this rare, and a short gap avoids a burst of parallel-looking calls.
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
    }

    private static IEnumerable<(List<Movie> Movies, List<Show> Shows)> Batch<Movie, Show>(
        List<Movie> movies,
        List<Show> shows)
    {
        var movieIndex = 0;
        var showIndex = 0;

        while (movieIndex < movies.Count || showIndex < shows.Count)
        {
            var movieBatch = movies.Skip(movieIndex).Take(PushBatchSize).ToList();
            var showBatch = shows.Skip(showIndex).Take(PushBatchSize).ToList();

            movieIndex += movieBatch.Count;
            showIndex += showBatch.Count;

            yield return (movieBatch, showBatch);
        }
    }

    private static Dictionary<string, DateTimeOffset> Newest(IReadOnlyList<WatchEvent> events)
    {
        var facts = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in events)
        {
            if (!facts.TryGetValue(item.Key, out var at) || item.At > at)
            {
                facts[item.Key] = item.At;
            }
        }

        return facts;
    }

    private static Dictionary<string, DateTimeOffset> Newest(IReadOnlyList<WatchlistChange> items)
    {
        var facts = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (!facts.TryGetValue(item.Key, out var at) || item.At > at)
            {
                facts[item.Key] = item.At;
            }
        }

        return facts;
    }

    private static string? ReadNextCursor(JsonElement root)
    {
        if (root.TryGetProperty("pagination", out var pagination) &&
            pagination.TryGetProperty("next_cursor", out var cursor) &&
            cursor.ValueKind == JsonValueKind.String)
        {
            return cursor.GetString();
        }

        return null;
    }

    private static bool TryReadImdb(JsonElement media, out string imdbId)
    {
        imdbId = "";

        if (!media.TryGetProperty("ids", out var ids) ||
            !ids.TryGetProperty("imdb", out var imdb) ||
            imdb.GetString() is not { Length: > 2 } value ||
            !value.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        imdbId = value;
        return true;
    }

    private static string? ReadString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static long ReadLong(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : 0;

    private static double? ReadDouble(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number)
            ? number
            : null;

    private static DateTimeOffset? ReadDate(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: > 0 } text)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string FormatDate(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string DescribeFailure(HttpStatusCode status, string body, string action)
    {
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return "MDBList rejected the API key. Disconnect MDBList and connect it again.";
        }

        if (status == HttpStatusCode.TooManyRequests)
        {
            return "MDBList's request limit is reached. Try again later.";
        }

        string? error = null;

        try
        {
            using var document = JsonDocument.Parse(body);
            error = ReadString(document.RootElement, "error");
        }
        catch (JsonException)
        {
        }

        return error is null
            ? $"Couldn't {action} (MDBList answered {(int)status})."
            : $"Couldn't {action} ({error}).";
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed class MdbListCursor
    {
        public DateTimeOffset? PushedThrough { get; set; }
    }

    private sealed class ImdbIds(string imdb)
    {
        public string Imdb { get; } = imdb;
    }

    private sealed class WatchedMovie
    {
        public ImdbIds Ids { get; set; } = new("");

        public string? WatchedAt { get; set; }
    }

    private sealed class WatchedShow
    {
        public ImdbIds Ids { get; set; } = new("");

        public List<WatchedSeason> Seasons { get; set; } = [];
    }

    private sealed class WatchedSeason
    {
        public int Number { get; set; }

        public string? WatchedAt { get; set; }

        public List<WatchedEpisode> Episodes { get; set; } = [];
    }

    private sealed class WatchedEpisode
    {
        public int Number { get; set; }

        public string? WatchedAt { get; set; }
    }

    private sealed class ListItem(ImdbIds ids)
    {
        public ImdbIds Ids { get; } = ids;
    }

    private sealed class RemoveMovie(ImdbIds ids)
    {
        public ImdbIds Ids { get; } = ids;
    }

    private sealed class RemoveShow
    {
        public ImdbIds Ids { get; set; } = new("");

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RemoveSeason>? Seasons { get; set; }
    }

    private sealed class RemoveSeason
    {
        public int Number { get; set; }

        public List<RemoveEpisode> Episodes { get; set; } = [];
    }

    private sealed class RemoveEpisode(int number)
    {
        public int Number { get; } = number;
    }
}

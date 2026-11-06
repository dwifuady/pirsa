using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pirsa.Helpers;

namespace Pirsa.Services.Sync.Providers;

/// <summary>
/// Simkl's AUTH V2 device flow. The user approves an 8-character code at
/// simkl.com/pin; Pirsa keeps the short-lived access token and the refresh
/// token in the encrypted credential file.
/// </summary>
public sealed class SimklSyncProvider : ISyncProvider
{
    /// <summary>
    /// Register at https://simkl.com/settings/developer/ and paste the client
    /// id here. Client ids are public; there is no secret to ship.
    /// Empty means the connect form asks for one.
    /// </summary>
    internal const string BuiltInClientId = "";

    internal const string AppName = "pirsa";

    /// <summary>Overridable so the flow can be exercised against a local server.</summary>
    internal static Uri ApiBaseUri { get; set; } = new("https://api.simkl.com");

    public SyncProviderDescriptor Descriptor { get; } = new(
        Id: "simkl",
        Name: "Simkl",
        Description: "Keep watched history and Plan to Watch in step with your Simkl account.",
        Coverage: "Movies · Series",
        Capabilities: SyncCapabilities.Watched | SyncCapabilities.Watchlist,
        AuthKind: SyncAuthKind.OAuthDevice,
        ConnectLabel: "Connect Simkl",
        Hint: "Pirsa opens simkl.com/pin with a short code. You approve it there; your Simkl password is never shared.",
        AsksForClientId: string.IsNullOrEmpty(BuiltInClientId));

    public async Task<ISyncSession> ConnectAsync(
        SyncCredentials credentials,
        IProgress<SyncAuthPrompt>? prompt = null,
        CancellationToken cancellationToken = default)
    {
        var clientId = string.IsNullOrWhiteSpace(credentials.ClientId)
            ? BuiltInClientId
            : credentials.ClientId.Trim();

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new StremioException(
                "Paste the client id from simkl.com/settings/developer first.");
        }

        var baseUri = ApiBaseUri;

        // Restoring a saved connection: the token pair is already there.
        if (!string.IsNullOrWhiteSpace(credentials.Token))
        {
            var existing = new SimklSyncSession(
                baseUri,
                clientId,
                credentials.Token,
                credentials.RefreshToken ?? "",
                credentials.ExpiresAt);

            await existing.LoadAccountAsync(cancellationToken);
            return existing;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        var device = await RequestDeviceCodeAsync(http, clientId, cancellationToken);

        prompt?.Report(new SyncAuthPrompt(
            device.UserCode,
            device.VerificationUri,
            device.VerificationUriComplete,
            TimeSpan.FromSeconds(device.ExpiresIn > 0 ? device.ExpiresIn : 900)));

        var token = await PollForTokenAsync(http, clientId, device, cancellationToken)
            ?? throw new StremioException("Simkl did not approve the sign-in in time. Try again.");

        var session = new SimklSyncSession(
            baseUri,
            clientId,
            token.AccessToken,
            token.RefreshToken ?? "",
            DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 604800));

        await session.LoadAccountAsync(cancellationToken);
        return session;
    }

    private static async Task<DeviceCode> RequestDeviceCodeAsync(
        HttpClient http,
        string clientId,
        CancellationToken cancellationToken)
    {
        var (status, body) = await PostFormAsync(
            http,
            new Uri(ApiBaseUri, "/oauth2/device").ToString(),
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = "media:read media:write",
            },
            cancellationToken);

        if (status == HttpStatusCode.Unauthorized)
        {
            throw new StremioException(
                "Simkl rejected this client id. Use an AUTH V2 app registration.");
        }

        if (!IsSuccess(status))
        {
            throw new StremioException($"Simkl couldn't start the sign-in ({status}).");
        }

        return JsonSerializer.Deserialize<DeviceCode>(body, JsonOptions)
            ?? throw new StremioException("Simkl returned an empty device code.");
    }

    private static async Task<TokenPair?> PollForTokenAsync(
        HttpClient http,
        string clientId,
        DeviceCode device,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(3, device.Interval));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn > 0 ? device.ExpiresIn : 900);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, cancellationToken);

            var (status, body) = await PostFormAsync(
                http,
                new Uri(ApiBaseUri, "/oauth2/token").ToString(),
                new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = clientId,
                    ["device_code"] = device.Code,
                },
                cancellationToken);

            if (IsSuccess(status))
            {
                return JsonSerializer.Deserialize<TokenPair>(body, JsonOptions);
            }

            var error = ReadError(body);

            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "expired_token":
                    throw new StremioException("The Simkl code expired. Connect again.");
                case "invalid_client":
                    throw new StremioException(
                        "Simkl rejected this client id. Use an AUTH V2 app registration.");
                default:
                    if (status == HttpStatusCode.Unauthorized)
                    {
                        throw new StremioException(
                            "Simkl rejected this client id. Use an AUTH V2 app registration.");
                    }

                    throw new StremioException(
                        error is null
                            ? $"Simkl couldn't finish the sign-in ({status})."
                            : $"Simkl answered {error}.");
            }
        }

        return null;
    }

    internal static string? ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error)
                ? error.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    internal static async Task<(HttpStatusCode Status, string Body)> PostFormAsync(
        HttpClient http,
        string url,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content,
        };

        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, cancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    internal static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    internal static string UserAgent =>
        $"pirsa/{(AppInfo.HasVersion ? AppInfo.Version : "1.0")}";

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed class DeviceCode
    {
        [JsonPropertyName("device_code")]
        public string Code { get; set; } = "";

        [JsonPropertyName("user_code")]
        public string UserCode { get; set; } = "";

        [JsonPropertyName("verification_uri")]
        public string VerificationUri { get; set; } = "https://simkl.com/pin";

        [JsonPropertyName("verification_uri_complete")]
        public string? VerificationUriComplete { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("interval")]
        public int Interval { get; set; } = 5;
    }

    private sealed class TokenPair
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = "";

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}

/// <summary>
/// One approved Simkl grant. Pulls with the two-phase activities model and
/// pushes only the facts newer than the last successful push.
/// </summary>
internal sealed class SimklSyncSession : ISyncSession
{

    private const int PushBatchSize = 100;
    private static readonly TimeSpan MinutesBeforeExpiryToRefresh = TimeSpan.FromDays(1);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly Uri _baseUri;
    private readonly string _clientId;

    private string _accessToken;
    private string _refreshToken;
    private DateTimeOffset? _expiresAt;
    private string? _pendingActivities;
    private SimklCursor? _cursor;
    private SyncCredentials? _updatedCredentials;

    public SimklSyncSession(
        Uri baseUri,
        string clientId,
        string accessToken,
        string refreshToken,
        DateTimeOffset? expiresAt)
    {
        _baseUri = baseUri;
        _clientId = clientId;
        _accessToken = accessToken;
        _refreshToken = refreshToken;
        _expiresAt = expiresAt;

        // The engine persists this after a successful connect, so a fresh
        // token pair is written without waiting for the first 401.
        _updatedCredentials = new SyncCredentials(
            ClientId: clientId,
            Token: accessToken,
            RefreshToken: refreshToken,
            ExpiresAt: expiresAt);
    }

    public string AccountLabel { get; private set; } = "Simkl";

    public string? Cursor
    {
        get => _cursor is null
            ? null
            : JsonSerializer.Serialize(_cursor, SimklSyncProvider.JsonOptions);
        set => _cursor = string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<SimklCursor>(value, SimklSyncProvider.JsonOptions);
    }

    public SyncCredentials? UpdatedCredentials => _updatedCredentials;

    /// <summary>Reads the display name once, on connect and on restore.</summary>
    public async Task LoadAccountAsync(CancellationToken cancellationToken)
    {
        var (status, body) = await SendAsync(
            HttpMethod.Get,
            "/users/settings",
            content: null,
            authorize: true,
            cancellationToken: cancellationToken);

        if (!SimklSyncProvider.IsSuccess(status))
        {
            throw new StremioException(DescribeFailure(status, body, "load the Simkl account"));
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("user", out var user) &&
                user.TryGetProperty("name", out var name) &&
                name.GetString() is { Length: > 0 } userName)
            {
                AccountLabel = userName;
            }
        }
        catch (JsonException)
        {
            // The name is cosmetic; a successful sync still works without it.
        }
    }

    public async Task<RemoteSnapshot> PullAsync(CancellationToken cancellationToken = default)
    {
        var activities = await GetActivitiesAsync(cancellationToken);

        if (_cursor?.Activities is { Length: > 0 } saved &&
            string.Equals(saved, activities, StringComparison.Ordinal))
        {
            // Nothing moved. Do not touch /sync/all-items.
            _pendingActivities = activities;
            return RemoteSnapshot.Empty;
        }

        var dateFrom = _cursor?.Activities;
        var watched = new List<WatchEvent>();
        var watchlist = new List<WatchlistChange>();

        foreach (var type in new[] { "shows", "movies" })
        {
            var body = await GetAllItemsAsync(type, dateFrom, cancellationToken);
            ParseAllItems(type, body, watched, watchlist);
        }

        _pendingActivities = activities;
        return new RemoteSnapshot(watched, watchlist);
    }

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
            // ">=" re-sends the newest item once. Simkl treats a repeated
            // watch as a no-op, and it covers two changes sharing a timestamp.
            newWatched = local.Watched.Where(item => item.At >= since).ToList();
            newWatchlist = local.Watchlist.Where(item => item.At >= since).ToList();
        }
        else
        {
            // First push after connecting. Do not send back the library that
            // was just pulled: only local items Simkl does not have, or has an
            // older copy of, are new information.
            var remoteWatched = NewestWatchFacts(remote.Watched);
            var remoteWatchlist = NewestWatchlistFacts(remote.Watchlist);

            newWatched = local.Watched
                .Where(item => !remoteWatched.TryGetValue(item.Key, out var at) || item.At > at)
                .ToList();

            newWatchlist = local.Watchlist
                .Where(item => !remoteWatchlist.TryGetValue(item.Key, out var at) || item.At > at)
                .ToList();
        }

        await PushHistoryAsync(newWatched, cancellationToken);
        await PushWatchlistAsync(newWatchlist, cancellationToken);
        var newUnwatched = threshold is { } tombstoneSince
            ? local.Unwatched.Where(item => item.At >= tombstoneSince).ToList()
            : [.. local.Unwatched];

        var newWatchlistRemoved = threshold is { } listTombstoneSince
            ? local.WatchlistRemoved.Where(item => item.At >= listTombstoneSince).ToList()
            : [.. local.WatchlistRemoved];

        await PushRemovalsAsync(
            local,
            newUnwatched,
            newWatchlistRemoved,
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
            // Everything local is now mirrored, including rows that were
            // already on Simkl and therefore not sent. The next push only has
            // to carry changes newer than the newest local fact.
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

        _cursor = new SimklCursor
        {
            Activities = _pendingActivities ?? _cursor?.Activities,
            PushedThrough = pushedThrough == DateTimeOffset.MinValue
                ? _cursor?.PushedThrough
                : pushedThrough,
        };
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _tokenGate.Dispose();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ pull

    private async Task<string?> GetActivitiesAsync(CancellationToken cancellationToken)
    {
        var (status, body) = await SendAsync(
            HttpMethod.Get,
            "/sync/activities",
            content: null,
            authorize: true,
            cancellationToken: cancellationToken);

        if (!SimklSyncProvider.IsSuccess(status))
        {
            throw new StremioException(DescribeFailure(status, body, "check Simkl for changes"));
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.TryGetProperty("all", out var all)
                ? all.GetString()
                : null;
        }
        catch (JsonException)
        {
            throw new StremioException("Simkl returned an unreadable activities response.");
        }
    }

    private async Task<string> GetAllItemsAsync(
        string type,
        string? dateFrom,
        CancellationToken cancellationToken)
    {
        var extra = new Dictionary<string, string>
        {
            ["extended"] = "full",
            ["episode_watched_at"] = "yes",
            ["include_all_episodes"] = "yes",
        };

        if (dateFrom is { Length: > 0 })
        {
            extra["date_from"] = dateFrom;
        }

        var (status, body) = await SendAsync(
            HttpMethod.Get,
            $"/sync/all-items/{type}",
            extra,
            content: null,
            authorize: true,
            cancellationToken: cancellationToken);

        if (SimklSyncProvider.IsSuccess(status))
        {
            return body;
        }

        // A very large library can exceed the episode budget. The lighter
        // variant still carries the real per-episode rows.
        if (status == HttpStatusCode.BadRequest && body.Contains("max_items", StringComparison.Ordinal))
        {
            extra["include_all_episodes"] = "original";

            var retry = await SendAsync(
                HttpMethod.Get,
                $"/sync/all-items/{type}",
                extra,
                content: null,
                authorize: true,
                cancellationToken: cancellationToken);

            if (SimklSyncProvider.IsSuccess(retry.Status))
            {
                return retry.Body;
            }

            throw new StremioException(
                "This Simkl library is too large to pull in one go. Sync it once from a smaller account or remove finished shows.");
        }

        throw new StremioException(DescribeFailure(status, body, "read the Simkl library"));
    }

    private static void ParseAllItems(
        string type,
        string body,
        List<WatchEvent> watched,
        List<WatchlistChange> watchlist)
    {
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
            if (type == "movies")
            {
                if (document.RootElement.TryGetProperty("movies", out var movies))
                {
                    foreach (var item in movies.EnumerateArray())
                    {
                        ParseMovie(item, watched, watchlist);
                    }
                }

                return;
            }

            if (document.RootElement.TryGetProperty("shows", out var shows))
            {
                foreach (var item in shows.EnumerateArray())
                {
                    ParseShow(item, watched, watchlist);
                }
            }
        }
    }

    private static void ParseMovie(
        JsonElement item,
        List<WatchEvent> watched,
        List<WatchlistChange> watchlist)
    {
        if (!TryReadMedia(item, "movie", out var imdbId, out var title, out var poster))
        {
            return;
        }

        var status = ReadString(item, "status");
        var lastWatched = ReadDate(item, "last_watched_at");
        var addedAt = ReadDate(item, "added_to_watchlist_at");

        if (lastWatched is { } at)
        {
            watched.Add(new WatchEvent(
                imdbId,
                SyncMediaKind.Movie,
                null,
                null,
                WatchEventKind.Watched,
                at,
                title,
                poster,
                "movie"));
        }
        else if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) &&
                 addedAt is { } completedAt)
        {
            watched.Add(new WatchEvent(
                imdbId,
                SyncMediaKind.Movie,
                null,
                null,
                WatchEventKind.Watched,
                completedAt,
                title,
                poster,
                "movie"));
        }

        if (string.Equals(status, "plantowatch", StringComparison.OrdinalIgnoreCase))
        {
            watchlist.Add(new WatchlistChange(
                imdbId,
                SyncMediaKind.Movie,
                true,
                addedAt ?? lastWatched ?? DateTimeOffset.UtcNow,
                title,
                poster,
                "movie"));
        }
    }

    private static void ParseShow(
        JsonElement item,
        List<WatchEvent> watched,
        List<WatchlistChange> watchlist)
    {
        if (!TryReadMedia(item, "show", out var imdbId, out var title, out var poster))
        {
            return;
        }

        var status = ReadString(item, "status");
        var lastWatched = ReadDate(item, "last_watched_at");
        var addedAt = ReadDate(item, "added_to_watchlist_at");

        if (item.TryGetProperty("seasons", out var seasons) &&
            seasons.ValueKind == JsonValueKind.Array)
        {
            foreach (var season in seasons.EnumerateArray())
            {
                if (!season.TryGetProperty("number", out var seasonNumber) ||
                    !seasonNumber.TryGetInt32(out var seasonIndex) ||
                    !season.TryGetProperty("episodes", out var episodes) ||
                    episodes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var episode in episodes.EnumerateArray())
                {
                    if (!episode.TryGetProperty("number", out var number) ||
                        !number.TryGetInt32(out var episodeIndex) ||
                        episodeIndex <= 0)
                    {
                        continue;
                    }

                    if (episode.TryGetProperty("watched", out var watchedFlag) &&
                        watchedFlag.ValueKind == JsonValueKind.False)
                    {
                        continue;
                    }

                    var at = ReadDate(episode, "watched_at")
                        ?? lastWatched
                        ?? addedAt
                        ?? DateTimeOffset.UtcNow;

                    watched.Add(new WatchEvent(
                        imdbId,
                        SyncMediaKind.Series,
                        seasonIndex,
                        episodeIndex,
                        WatchEventKind.Watched,
                        at,
                        title,
                        poster,
                        "series"));
                }
            }
        }

        if (string.Equals(status, "plantowatch", StringComparison.OrdinalIgnoreCase))
        {
            watchlist.Add(new WatchlistChange(
                imdbId,
                SyncMediaKind.Series,
                true,
                addedAt ?? DateTimeOffset.UtcNow,
                title,
                poster,
                "series"));
        }
    }

    private static bool TryReadMedia(
        JsonElement item,
        string property,
        out string imdbId,
        out string? title,
        out string? poster)
    {
        imdbId = "";
        title = null;
        poster = null;

        if (!item.TryGetProperty(property, out var media) ||
            !media.TryGetProperty("ids", out var ids) ||
            !ids.TryGetProperty("imdb", out var imdb) ||
            imdb.GetString() is not { Length: > 2 } value ||
            !value.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        imdbId = value;
        title = ReadString(media, "title");
        poster = BuildPoster(ReadString(media, "poster"));
        return true;
    }

    private static string? BuildPoster(string? poster)
    {
        if (string.IsNullOrWhiteSpace(poster))
        {
            return null;
        }

        return poster.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? poster
            : $"https://simkl.in/posters/{poster}_m.webp";
    }

    // ------------------------------------------------------------------ push

    private async Task PushHistoryAsync(
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
            .Select(group => new HistoryMovie
            {
                Ids = new ImdbIds(group.Key),
                WatchedAt = FormatDate(group.Max(item => item.At)),
            })
            .ToList();

        var shows = watched
            .Where(item => item.Kind == SyncMediaKind.Series)
            .GroupBy(item => item.ImdbId, StringComparer.OrdinalIgnoreCase)
            .Select(showGroup => new HistoryShow
            {
                Ids = new ImdbIds(showGroup.Key),
                Seasons = [.. showGroup
                    .Where(item => item.Season is > 0 && item.Episode is > 0)
                    .GroupBy(item => item.Season!.Value)
                    .OrderBy(season => season.Key)
                    .Select(season => new HistorySeason
                    {
                        Number = season.Key,
                        Episodes = [.. season
                            .OrderBy(item => item.Episode)
                            .Select(item => new HistoryEpisode
                            {
                                Number = item.Episode!.Value,
                                WatchedAt = FormatDate(item.At),
                            })],
                    })],
            })
            .Where(show => show.Seasons.Count > 0)
            .ToList();

        foreach (var batch in Batch(movies, shows, PushBatchSize))
        {
            var (status, body) = await SendAsync(
                HttpMethod.Post,
                "/sync/history",
                content: () => JsonContent(new { movies = batch.Movies, shows = batch.Shows }),
                authorize: true,
                cancellationToken: cancellationToken);

            if (!SimklSyncProvider.IsSuccess(status))
            {
                throw new StremioException(DescribeFailure(status, body, "save watched state"));
            }

            await PostBatchDelayAsync(cancellationToken);
        }
    }

    private async Task PushWatchlistAsync(
        IReadOnlyList<WatchlistChange> watchlist,
        CancellationToken cancellationToken)
    {
        if (watchlist.Count == 0)
        {
            return;
        }

        var movies = watchlist
            .Where(item => item.Kind == SyncMediaKind.Movie)
            .GroupBy(item => item.ImdbId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ListItem(new ImdbIds(group.Key)))
            .ToList();

        var shows = watchlist
            .Where(item => item.Kind == SyncMediaKind.Series)
            .GroupBy(item => item.ImdbId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ListItem(new ImdbIds(group.Key)))
            .ToList();

        foreach (var batch in Batch(movies, shows, PushBatchSize))
        {
            var (status, body) = await SendAsync(
                HttpMethod.Post,
                "/sync/add-to-list",
                content: () => JsonContent(new { movies = batch.Movies, shows = batch.Shows }),
                authorize: true,
                cancellationToken: cancellationToken);

            if (!SimklSyncProvider.IsSuccess(status))
            {
                throw new StremioException(DescribeFailure(status, body, "save the watchlist"));
            }

            await PostBatchDelayAsync(cancellationToken);
        }
    }

    private async Task PushRemovalsAsync(
        SyncSnapshot local,
        IReadOnlyList<WatchEvent> unwatched,
        IReadOnlyList<WatchlistChange> watchlistRemoved,
        CancellationToken cancellationToken)
    {
        // Removing a title from the local watchlist must not delete a watch
        // history that still exists on Simkl. Only propagate the removal when
        // the title is not watched locally; a watched one stays in the Simkl
        // library and can be removed there.
        var watchedMovies = local.Watched
            .Where(item => item.Kind == SyncMediaKind.Movie)
            .Select(item => item.ImdbId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var watchedShows = local.Watched
            .Where(item => item.Kind == SyncMediaKind.Series)
            .Select(item => item.ImdbId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removalMovies = unwatched
            .Where(item => item.Kind == SyncMediaKind.Movie)
            .Select(item => new RemovalMovie(new ImdbIds(item.ImdbId)))
            .Concat(watchlistRemoved
                .Where(item => item.Kind == SyncMediaKind.Movie && !watchedMovies.Contains(item.ImdbId))
                .Select(item => new RemovalMovie(new ImdbIds(item.ImdbId))))
            .GroupBy(item => item.Ids.Imdb, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var removalShows = unwatched
            .Where(item => item.Kind == SyncMediaKind.Series && item.Season is > 0 && item.Episode is > 0)
            .GroupBy(item => item.ImdbId, StringComparer.OrdinalIgnoreCase)
            .Select(showGroup => new RemovalShow
            {
                Ids = new ImdbIds(showGroup.Key),
                Seasons = [.. showGroup
                    .GroupBy(item => item.Season!.Value)
                    .OrderBy(season => season.Key)
                    .Select(season => new RemovalSeason
                    {
                        Number = season.Key,
                        Episodes = [.. season
                            .OrderBy(item => item.Episode)
                            .Select(item => new RemovalEpisode(item.Episode!.Value))],
                    })],
            })
            .ToList();

        // A whole-show removal (watchlist) deletes the library entry; an
        // episode removal only unmarks those episodes.
        var wholeShowRemovals = watchlistRemoved
            .Where(item => item.Kind == SyncMediaKind.Series && !watchedShows.Contains(item.ImdbId))
            .Select(item => new RemovalShow { Ids = new ImdbIds(item.ImdbId) })
            .ToList();

        // A whole-show removal wins over an episode removal for the same show.
        var allShows = wholeShowRemovals
            .Concat(removalShows)
            .GroupBy(item => item.Ids.Imdb, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (removalMovies.Count == 0 && allShows.Count == 0)
        {
            return;
        }

        foreach (var batch in Batch(removalMovies, allShows, PushBatchSize))
        {
            var (status, body) = await SendAsync(
                HttpMethod.Post,
                "/sync/history/remove",
                content: () => JsonContent(new { movies = batch.Movies, shows = batch.Shows }),
                authorize: true,
                cancellationToken: cancellationToken);

            if (!SimklSyncProvider.IsSuccess(status))
            {
                throw new StremioException(DescribeFailure(status, body, "remove watched state"));
            }

            await PostBatchDelayAsync(cancellationToken);
        }
    }

    // ------------------------------------------------------------------ http

    private Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method,
        string path,
        bool authorize,
        CancellationToken cancellationToken) =>
        SendAsync(method, path, new Dictionary<string, string>(), content: null, authorize: authorize, cancellationToken: cancellationToken);

    private Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method,
        string path,
        Func<HttpContent?>? content,
        bool authorize,
        CancellationToken cancellationToken) =>
        SendAsync(
            method,
            path,
            new Dictionary<string, string>(),
            content,
            authorize,
            cancellationToken);

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string> query,
        Func<HttpContent?>? content,
        bool authorize,
        CancellationToken cancellationToken)
    {
        if (authorize)
        {
            await EnsureFreshTokenAsync(cancellationToken);
        }

        var response = await SendCoreAsync(
            method,
            path,
            query,
            content,
            authorize,
            cancellationToken);

        // One retry after a refresh: the token may have been rotated by
        // another process or simply expired while the app was closed.
        if (authorize && response.Status == HttpStatusCode.Unauthorized &&
            !string.IsNullOrWhiteSpace(_refreshToken))
        {
            await RefreshTokenAsync(cancellationToken);
            response = await SendCoreAsync(
                method,
                path,
                query,
                content,
                authorize,
                cancellationToken);
        }

        return response;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendCoreAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string> query,
        Func<HttpContent?>? content,
        bool authorize,
        CancellationToken cancellationToken)
    {
        var uri = BuildUri(path, query);
        var response = await SendOnceAsync(
            method,
            uri,
            content,
            authorize,
            cancellationToken);

        // A short daily-quota 429 is worth one retry. Longer waits belong to
        // the user or the next sync.
        if (response.Status == HttpStatusCode.TooManyRequests &&
            response.RetryAfter is { } retryAfter &&
            retryAfter <= TimeSpan.FromSeconds(30))
        {
            await Task.Delay(retryAfter, cancellationToken);

            response = await SendOnceAsync(
                method,
                uri,
                content,
                authorize,
                cancellationToken);
        }

        return (response.Status, response.Body);
    }

    private async Task<(HttpStatusCode Status, string Body, TimeSpan? RetryAfter)> SendOnceAsync(
        HttpMethod method,
        Uri uri,
        Func<HttpContent?>? content,
        bool authorize,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);

        if (content is not null)
        {
            request.Content = content();
        }

        request.Headers.UserAgent.ParseAdd(SimklSyncProvider.UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (authorize)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        }

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
            $"client_id={Uri.EscapeDataString(_clientId)}",
            $"app-name={Uri.EscapeDataString(SimklSyncProvider.AppName)}",
            $"app-version={Uri.EscapeDataString(AppInfo.HasVersion ? AppInfo.Version : "1.0")}",
        };

        foreach (var pair in query)
        {
            parameters.Add($"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");
        }

        return new Uri(_baseUri, $"{path}?{string.Join("&", parameters)}");
    }

    private async Task EnsureFreshTokenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_refreshToken) || _expiresAt is null)
        {
            return;
        }

        if (_expiresAt > DateTimeOffset.UtcNow.Add(MinutesBeforeExpiryToRefresh))
        {
            return;
        }

        await RefreshTokenAsync(cancellationToken);
    }

    private async Task RefreshTokenAsync(CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            var (status, body) = await SimklSyncProvider.PostFormAsync(
                _http,
                new Uri(_baseUri, "/oauth2/token").ToString(),
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = _clientId,
                    ["refresh_token"] = _refreshToken,
                },
                cancellationToken);

            if (!SimklSyncProvider.IsSuccess(status))
            {
                throw new StremioException(
                    "Simkl sign-in has expired. Disconnect Simkl and connect it again.");
            }

            var token = JsonSerializer.Deserialize<TokenPair>(body, SimklSyncProvider.JsonOptions);
            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                throw new StremioException("Simkl returned an empty refreshed token.");
            }

            _accessToken = token.AccessToken;
            _refreshToken = token.RefreshToken ?? _refreshToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 604800);
            _updatedCredentials = new SyncCredentials(
                ClientId: _clientId,
                Token: _accessToken,
                RefreshToken: _refreshToken,
                ExpiresAt: _expiresAt);
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    // ------------------------------------------------------------------ helpers

    private static HttpContent JsonContent(object body) =>
        new StringContent(
            JsonSerializer.Serialize(body, SimklSyncProvider.JsonOptions),
            Encoding.UTF8,
            "application/json");

    private static async Task PostBatchDelayAsync(CancellationToken cancellationToken)
    {
        // Simkl allows one POST per second. Batches are large, so this is rare.
        await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellationToken);
    }

    private static IEnumerable<(List<Movie> Movies, List<Show> Shows)> Batch<Movie, Show>(
        List<Movie> movies,
        List<Show> shows,
        int size)
    {
        var movieIndex = 0;
        var showIndex = 0;

        while (movieIndex < movies.Count || showIndex < shows.Count)
        {
            var movieBatch = movies.Skip(movieIndex).Take(size).ToList();
            var showBatch = shows.Skip(showIndex).Take(size).ToList();

            movieIndex += movieBatch.Count;
            showIndex += showBatch.Count;

            yield return (movieBatch, showBatch);
        }
    }

    private static string? ReadString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
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
        if (status == HttpStatusCode.TooManyRequests)
        {
            return "Simkl's request limit is reached. Try again later.";
        }

        if (status == HttpStatusCode.Unauthorized)
        {
            return "Simkl sign-in has expired. Disconnect Simkl and connect it again.";
        }

        var error = SimklSyncProvider.ReadError(body);
        return error is null
            ? $"Couldn't {action} (Simkl answered {(int)status})."
            : $"Couldn't {action} ({error}).";
    }

    private static Dictionary<string, DateTimeOffset> NewestWatchFacts(
        IReadOnlyList<WatchEvent> events)
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

    private static Dictionary<string, DateTimeOffset> NewestWatchlistFacts(
        IReadOnlyList<WatchlistChange> items)
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

    private sealed class SimklCursor
    {
        public string? Activities { get; set; }

        public DateTimeOffset? PushedThrough { get; set; }
    }

    private sealed class ImdbIds(string imdb)
    {
        public string Imdb { get; } = imdb;
    }

    private sealed class HistoryMovie
    {
        public ImdbIds Ids { get; set; } = new("");

        public string? WatchedAt { get; set; }
    }

    private sealed class HistoryShow
    {
        public ImdbIds Ids { get; set; } = new("");

        public List<HistorySeason> Seasons { get; set; } = [];
    }

    private sealed class HistorySeason
    {
        public int Number { get; set; }

        public List<HistoryEpisode> Episodes { get; set; } = [];
    }

    private sealed class HistoryEpisode
    {
        public int Number { get; set; }

        public string? WatchedAt { get; set; }
    }

    private sealed class ListItem(ImdbIds ids)
    {
        public ImdbIds Ids { get; } = ids;

        public string To { get; } = "plantowatch";
    }

    private sealed class RemovalMovie(ImdbIds ids)
    {
        public ImdbIds Ids { get; } = ids;
    }

    private sealed class RemovalShow
    {
        public ImdbIds Ids { get; set; } = new("");

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RemovalSeason>? Seasons { get; set; }
    }

    private sealed class RemovalSeason
    {
        public int Number { get; set; }

        public List<RemovalEpisode> Episodes { get; set; } = [];
    }

    private sealed class RemovalEpisode(int number)
    {
        public int Number { get; } = number;
    }

    private sealed class TokenPair
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = "";

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}

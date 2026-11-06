using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pirsa.Models;

namespace Pirsa.Services;

/// <summary>
/// Looks up intro, recap, outro and post-credits timestamps for a title.
/// Timestamps come from <see href="https://introdb.app">IntroDB</see>, a
/// community database addressed by IMDb id. Results are cached in memory, so
/// switching sources on the same episode does not hit the service again.
/// </summary>
public static class SkipSegmentsService
{
    private const string BaseUrl = "https://api.introdb.app";

    private static readonly HttpClient Http = CreateHttpClient();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly TimeSpan FoundLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan MissingLifetime = TimeSpan.FromMinutes(10);
    private const int MaxCacheEntries = 128;

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Segments for the request's title, or an empty list when the title has no
    /// IMDb id, is offline, or has no submissions yet.
    /// </summary>
    public static async Task<IReadOnlyList<SkipSegment>> GetAsync(
        PlaybackRequest request,
        CancellationToken cancellationToken = default)
    {
        var imdbId = ResolveImdbId(request);
        if (imdbId is null)
        {
            return [];
        }

        var isMovie = IsMovie(request);
        var season = request.Season;
        var episode = request.Episode;

        if (!isMovie && (season <= 0 || episode <= 0))
        {
            return [];
        }

        var cacheKey = isMovie
            ? $"movie:{imdbId}"
            : $"episode:{imdbId}:{season}:{episode}";

        if (TryGetCached(cacheKey, out var cached))
        {
            return cached;
        }

        var segments = await FetchAsync(imdbId, isMovie, season, episode, cancellationToken);
        Store(cacheKey, segments);
        return segments;
    }

    /// <summary>The IMDb id inside a Stremio meta or video id, or null for a custom id.</summary>
    public static string? ResolveImdbId(PlaybackRequest request) =>
        NormalizeImdbId(request.Meta.Id) ??
        NormalizeImdbId(request.VideoId.Split(':')[0]);

    /// <summary>True for a film, false for a series episode (including specials).</summary>
    public static bool IsMovie(PlaybackRequest request) =>
        request.Type.Equals("movie", StringComparison.OrdinalIgnoreCase) ||
        (request.Season <= 0 && request.Episode <= 0);

    private static async Task<IReadOnlyList<SkipSegment>> FetchAsync(
        string imdbId,
        bool isMovie,
        int season,
        int episode,
        CancellationToken cancellationToken)
    {
        var url = isMovie
            ? $"{BaseUrl}/segments?imdb_id={Uri.EscapeDataString(imdbId)}&is_movie=true"
            : $"{BaseUrl}/segments?imdb_id={Uri.EscapeDataString(imdbId)}&season={season}&episode={episode}";

        try
        {
            using var response = await Http.GetAsync(url, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return [];
            }

            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<IntroDbSegmentsResponse>(
                JsonOptions,
                cancellationToken);

            return payload is null ? [] : BuildSegments(payload, isMovie);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Skip data is an enhancement: a slow or broken service must never
            // interrupt playback.
            return [];
        }
    }

    private static IReadOnlyList<SkipSegment> BuildSegments(
        IntroDbSegmentsResponse response,
        bool isMovie)
    {
        if (isMovie)
        {
            var credits = ToSegment(response.Outro, SkipSegmentKind.MovieCredits);
            var scene = ToSegment(response.PostCredits, SkipSegmentKind.PostCredits);

            // End-credit skipping must stop before the post-credits scene even
            // when the two submissions overlap. A scene that starts before the
            // credits do is a mid-credits scene, and trimming to it would make
            // the credits interval invalid, so leave both alone.
            if (credits is not null &&
                scene is not null &&
                scene.StartMs > credits.StartMs &&
                scene.StartMs < credits.EndMs &&
                scene.EndMs > credits.StartMs)
            {
                credits = credits with { EndMs = scene.StartMs };
            }

            return SkipSegmentRules.Merge(
                new SkipSegment?[] { credits, scene }.OfType<SkipSegment>().ToList(),
                [],
                isMovie: true);
        }

        return SkipSegmentRules.Merge(
            new SkipSegment?[]
            {
                ToSegment(response.Intro, SkipSegmentKind.Intro),
                ToSegment(response.Recap, SkipSegmentKind.Recap),
                ToSegment(response.Outro, SkipSegmentKind.Outro),
                ToSegment(response.PostCredits, SkipSegmentKind.PostCredits),
            }.OfType<SkipSegment>().ToList(),
            [],
            isMovie: false);
    }

    private static SkipSegment? ToSegment(IntroDbSegment? segment, SkipSegmentKind kind)
    {
        if (segment is null)
        {
            return null;
        }

        var start = segment.StartMs ??
                    (segment.StartSec is { } startSeconds
                        ? (long)Math.Round(startSeconds * 1000)
                        : null);
        var end = segment.EndMs ??
                  (segment.EndSec is { } endSeconds
                      ? (long)Math.Round(endSeconds * 1000)
                      : null);

        if (start is not { } startMs ||
            end is not { } endMs ||
            startMs < 0 ||
            endMs <= startMs)
        {
            return null;
        }

        return new SkipSegment(startMs, endMs, kind, "introdb");
    }

    private static string? NormalizeImdbId(string? id)
    {
        var value = id?.Trim();
        return value is { Length: > 2 } &&
               value.StartsWith("tt", StringComparison.OrdinalIgnoreCase) &&
               value.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0
            ? value
            : null;
    }

    private static bool TryGetCached(string key, out IReadOnlyList<SkipSegment> segments)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var entry) &&
                entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                segments = entry.Segments;
                return true;
            }

            Cache.Remove(key);
        }

        segments = [];
        return false;
    }

    private static void Store(string key, IReadOnlyList<SkipSegment> segments)
    {
        lock (CacheGate)
        {
            if (Cache.Count >= MaxCacheEntries)
            {
                var stale = Cache
                    .OrderBy(entry => entry.Value.ExpiresAt)
                    .Take(Cache.Count - MaxCacheEntries + 1)
                    .Select(entry => entry.Key)
                    .ToList();

                foreach (var staleKey in stale)
                {
                    Cache.Remove(staleKey);
                }
            }

            Cache[key] = new CacheEntry(
                segments,
                DateTimeOffset.UtcNow + (segments.Count > 0 ? FoundLifetime : MissingLifetime));
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Pirsa/1.0");
        return client;
    }

    private sealed record CacheEntry(IReadOnlyList<SkipSegment> Segments, DateTimeOffset ExpiresAt);

    private sealed class IntroDbSegmentsResponse
    {
        [JsonPropertyName("intro")]
        public IntroDbSegment? Intro { get; set; }

        [JsonPropertyName("recap")]
        public IntroDbSegment? Recap { get; set; }

        [JsonPropertyName("outro")]
        public IntroDbSegment? Outro { get; set; }

        [JsonPropertyName("post_credits")]
        public IntroDbSegment? PostCredits { get; set; }
    }

    private sealed class IntroDbSegment
    {
        [JsonPropertyName("start_ms")]
        public long? StartMs { get; set; }

        [JsonPropertyName("end_ms")]
        public long? EndMs { get; set; }

        [JsonPropertyName("start_sec")]
        public double? StartSec { get; set; }

        [JsonPropertyName("end_sec")]
        public double? EndSec { get; set; }
    }
}

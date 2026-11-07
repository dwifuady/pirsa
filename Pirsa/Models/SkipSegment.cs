namespace Pirsa.Models;

/// <summary>
/// The kind of stretch a skip button can jump over. The values mirror the
/// segment names IntroDB uses, plus the chapter labels a file can carry.
/// </summary>
public enum SkipSegmentKind
{
    /// <summary>An opening or title sequence.</summary>
    Intro,

    /// <summary>"Previously on…" recap.</summary>
    Recap,

    /// <summary>An episode ending, or a film's end credits.</summary>
    Outro,

    /// <summary>End credits reported for a film, which can be followed by a scene.</summary>
    MovieCredits,

    /// <summary>A scene after the credits. Never offered as a skip target itself.</summary>
    PostCredits,
}

/// <summary>
/// A stretch of a title viewers usually skip: an intro, a recap, an outro, or
/// the credits of a film. Times are milliseconds from the start of the file.
/// </summary>
public sealed record SkipSegment(
    long StartMs,
    long EndMs,
    SkipSegmentKind Kind,
    string Provider)
{
    /// <summary>True while [positionMs] is inside the segment.</summary>
    public bool Contains(long positionMs) => positionMs >= StartMs && positionMs < EndMs;
}

/// <summary>Where a skip press should land, and whether that is a post-credits scene.</summary>
public readonly record struct SkipTarget(long PositionMs, bool GoesToPostCredits);

/// <summary>
/// Shared rules for skip segments: which ones can be skipped, where they lead,
/// and how API data is combined with chapters from the playing file.
/// </summary>
public static class SkipSegmentRules
{
    public const long PostCreditsGapMs = 5_000;

    /// <summary>
    /// Resolves the seek target for a segment. End credits stop at a following
    /// post-credits scene when the provider knows about one, so the scene is not
    /// skipped as well.
    /// </summary>
    public static SkipTarget? FindTarget(
        SkipSegment segment,
        IReadOnlyList<SkipSegment> segments,
        long durationMs)
    {
        if (segment.Kind == SkipSegmentKind.PostCredits)
        {
            return null;
        }

        if (segment.StartMs < 0 || segment.EndMs <= segment.StartMs)
        {
            return null;
        }

        var scene = FindFollowingPostCredits(segment, segments, durationMs);

        var target = scene?.StartMs ?? segment.EndMs;
        if (durationMs > 0)
        {
            target = Math.Min(target, durationMs - 1);
        }

        return target <= segment.StartMs
            ? null
            : new SkipTarget(Math.Max(0, target), scene is not null);
    }

    /// <summary>
    /// The first post-credits scene that starts after [segment] ends, when it is
    /// inside the file. A scene the provider placed before the credits ends is
    /// ignored because seeking there would move backwards.
    /// </summary>
    private static SkipSegment? FindFollowingPostCredits(
        SkipSegment segment,
        IReadOnlyList<SkipSegment> segments,
        long durationMs)
    {
        if (segment.Kind is not (SkipSegmentKind.Outro or SkipSegmentKind.MovieCredits))
        {
            return null;
        }

        return segments
            .Where(candidate =>
                candidate.Kind == SkipSegmentKind.PostCredits &&
                candidate.StartMs >= segment.EndMs &&
                candidate.EndMs > candidate.StartMs &&
                (durationMs <= 0 || candidate.StartMs < durationMs))
            .MinBy(candidate => candidate.StartMs);
    }

    /// <summary>
    /// Combines provider data with chapters read from the file. Chapters describe
    /// this exact release, so they win per category; provider data fills gaps.
    /// For films the provider wins when it has anything, because it also knows
    /// about post-credits scenes.
    /// </summary>
    public static IReadOnlyList<SkipSegment> Merge(
        IReadOnlyList<SkipSegment> providerSegments,
        IReadOnlyList<SkipSegment> chapterSegments,
        bool isMovie)
    {
        if (chapterSegments.Count == 0)
        {
            return Sort(providerSegments);
        }

        if (providerSegments.Count == 0)
        {
            return Sort(chapterSegments);
        }

        if (isMovie)
        {
            return Sort(providerSegments);
        }

        var chosen = new Dictionary<string, SkipSegment>(StringComparer.Ordinal);
        var extras = new List<SkipSegment>();

        foreach (var segment in chapterSegments)
        {
            var category = CategoryOf(segment.Kind);
            if (category is null)
            {
                extras.Add(segment);
            }
            else
            {
                chosen.TryAdd(category, segment);
            }
        }

        foreach (var segment in providerSegments)
        {
            var category = CategoryOf(segment.Kind);
            if (category is null)
            {
                extras.Add(segment);
            }
            else
            {
                chosen.TryAdd(category, segment);
            }
        }

        extras.AddRange(chosen.Values);
        return Sort(extras);
    }

    /// <summary>
    /// The category two segments must share to be treated as the same part of
    /// the title. Post-credits segments are deliberately uncategorized.
    /// </summary>
    public static string? CategoryOf(SkipSegmentKind kind) => kind switch
    {
        SkipSegmentKind.Intro => "opening",
        SkipSegmentKind.Recap => "recap",
        SkipSegmentKind.Outro or SkipSegmentKind.MovieCredits => "ending",
        _ => null,
    };

    private static IReadOnlyList<SkipSegment> Sort(IEnumerable<SkipSegment> segments) =>
        segments
            .Where(segment => segment.EndMs > segment.StartMs && segment.StartMs >= 0)
            .OrderBy(segment => segment.StartMs)
            .ToList();
}

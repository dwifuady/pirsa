using System.Text.RegularExpressions;
using Pirsa.Models;
using Pirsa.Playback;

namespace Pirsa.Services;

/// <summary>
/// Turns a file's chapters into skip segments. Chapter titles are matched
/// case-insensitively, and only when the title is essentially the segment label
/// itself, so scene names like "The Opening" or "Alternate Ending" do not
/// produce false skip buttons.
/// </summary>
internal static class ChapterSkipSegments
{
    private const string Provider = "chapters";

    private const long MinDurationMs = 5_000;
    private const long MaxDurationMs = 240_000;

    private static readonly Regex OpeningPattern = new(
        @"^\s*(op(ening)?)(\s*(credits|theme))?\s*\d*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex EndingPattern = new(
        @"^\s*(ed|ending)(\s*(credits|theme))?\s*\d*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CreditsPattern = new(
        @"^\s*(end|closing)?\s*credits\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RecapPattern = new(
        @"^\s*(recap|previously(\s+on.*)?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex IntroPattern = new(
        @"^\s*intro\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PostCreditsPattern = new(
        @"(post|mid|after)[\s-]credits",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Segments named by [chapters], or an empty list when the file has too few
    /// chapters or the duration is not known yet. A chapter runs until the next
    /// one starts, or until the end of the file.
    /// </summary>
    public static IReadOnlyList<SkipSegment> FromChapters(
        IReadOnlyList<PlaybackChapter> chapters,
        long durationMs)
    {
        if (durationMs <= 0)
        {
            return [];
        }

        var ordered = chapters
            .Where(chapter => chapter.StartMs >= 0)
            .OrderBy(chapter => chapter.StartMs)
            .DistinctBy(chapter => chapter.StartMs)
            .ToList();

        if (ordered.Count < 2)
        {
            return [];
        }

        var classified = new List<ClassifiedChapter>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            var endMs = index + 1 < ordered.Count ? ordered[index + 1].StartMs : durationMs;
            classified.Add(new ClassifiedChapter(
                ordered[index],
                endMs,
                ClassifyTitle(ordered[index].Title)));
        }

        // "Intro" is only an opening when the file has no dedicated OP chapter;
        // otherwise it is a scene label and must not become a skip button.
        var hasExplicitOpening = classified.Any(chapter => chapter.Kind == ChapterKind.Opening);
        var segments = new List<SkipSegment>();

        foreach (var chapter in classified)
        {
            var kind = chapter.Kind;
            if (kind is null || (kind == ChapterKind.Intro && hasExplicitOpening))
            {
                continue;
            }

            var startMs = chapter.Chapter.StartMs;
            var endMs = chapter.EndMs;
            var length = endMs - startMs;

            if (length < MinDurationMs)
            {
                continue;
            }

            if (kind != ChapterKind.PostCredits && length > MaxDurationMs)
            {
                continue;
            }

            segments.Add(new SkipSegment(
                startMs,
                endMs,
                kind switch
                {
                    ChapterKind.Opening or ChapterKind.Intro => SkipSegmentKind.Intro,
                    ChapterKind.Ending => SkipSegmentKind.Outro,
                    ChapterKind.Recap => SkipSegmentKind.Recap,
                    _ => SkipSegmentKind.PostCredits,
                },
                Provider));
        }

        return segments;
    }

    private static ChapterKind? ClassifyTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        if (PostCreditsPattern.IsMatch(title))
        {
            return ChapterKind.PostCredits;
        }

        if (OpeningPattern.IsMatch(title))
        {
            return ChapterKind.Opening;
        }

        if (EndingPattern.IsMatch(title) || CreditsPattern.IsMatch(title))
        {
            return ChapterKind.Ending;
        }

        if (RecapPattern.IsMatch(title))
        {
            return ChapterKind.Recap;
        }

        return IntroPattern.IsMatch(title) ? ChapterKind.Intro : null;
    }

    private enum ChapterKind
    {
        Opening,
        Ending,
        Recap,
        Intro,
        PostCredits,
    }

    private sealed record ClassifiedChapter(
        PlaybackChapter Chapter,
        long EndMs,
        ChapterKind? Kind);
}

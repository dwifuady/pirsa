using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Pirsa.Helpers;

/// <summary>
/// One timed line from a subtitle file. Times are in the file's own timeline,
/// before the viewer's timing offset is applied.
/// </summary>
public readonly record struct SubtitleCue(long StartMs, long EndMs, string Text)
{
    public long DurationMs => EndMs - StartMs;
}

/// <summary>
/// Reads subtitle cue times and text out of the local files the player already
/// downloads. The lyric sync view needs cue starts and text, which the playback
/// engines expose only as whatever is on screen, so the page parses the file
/// directly. Supports SubRip, WebVTT and SubStation Alpha; anything else
/// (binary VobSub, MicroDVD) returns null and the feature stays hidden.
/// </summary>
public static class SubtitleCueFile
{
    private static readonly Regex AssTagRegex = new(@"\{[^}]*\}", RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex = new(@"<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex ExcessSpacesRegex = new(@"[ \t]{2,}", RegexOptions.Compiled);

    /// <summary>Parses a subtitle file, or returns null when it is not a supported text format.</summary>
    public static IReadOnlyList<SubtitleCue>? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = ReadText(path);
            return Parse(text, Path.GetExtension(path));
        }
        catch
        {
            // A malformed subtitle must not break playback or the timing panel.
            return null;
        }
    }

    /// <summary>Parses subtitle text. Exposed for tests and for callers with the bytes already in hand.</summary>
    public static IReadOnlyList<SubtitleCue>? Parse(string text, string? extension)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .TrimStart('\uFEFF');

        var cues = (extension?.ToLowerInvariant() switch
        {
            ".ass" or ".ssa" => ParseAss(normalized),
            _ => ParseCueBlocks(normalized),
        })
            .Where(cue => cue.EndMs > cue.StartMs && !string.IsNullOrWhiteSpace(cue.Text))
            .OrderBy(cue => cue.StartMs)
            .ToList();

        return cues.Count == 0 ? null : cues;
    }

    /// <summary>
    /// The index of the last cue that has started at <paramref name="timeMs"/>,
    /// or -1 before the first line. Cues are sorted by start, so this is a
    /// binary search; the lyric view calls it on every playhead update.
    /// </summary>
    public static int FindActiveIndex(IReadOnlyList<SubtitleCue> cues, long timeMs)
    {
        var low = 0;
        var high = cues.Count - 1;
        var result = -1;

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (cues[middle].StartMs <= timeMs)
            {
                result = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return result;
    }

    private static IEnumerable<SubtitleCue> ParseCueBlocks(string text)
    {
        var lines = text.Split('\n');
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index].Trim();
            if (!line.Contains("-->", StringComparison.Ordinal) ||
                !TryParseTimeRange(line, out var start, out var end))
            {
                // WEBVTT header, NOTE/STYLE/REGION blocks, cue ids and blank lines.
                index++;
                continue;
            }

            index++;

            // Read the text until a blank line or the next timing line. A
            // missing blank separator before the next cue is common enough to
            // handle here rather than dropping every cue after the first.
            var body = new System.Text.StringBuilder();
            while (index < lines.Length)
            {
                var next = lines[index].Trim();
                if (next.Length == 0)
                {
                    index++;
                    break;
                }

                if (next.Contains("-->", StringComparison.Ordinal))
                {
                    break;
                }

                if (IsCueIndexLine(next) &&
                    index + 1 < lines.Length &&
                    lines[index + 1].Contains("-->", StringComparison.Ordinal))
                {
                    break;
                }

                if (body.Length > 0)
                {
                    body.Append('\n');
                }

                body.Append(next);
                index++;
            }

            var cueText = CleanText(body.ToString());
            if (cueText.Length == 0)
            {
                continue;
            }

            yield return new SubtitleCue(start, end, cueText);
        }
    }

    private static bool IsCueIndexLine(string line)
    {
        if (line.Length == 0 || line.Length > 9)
        {
            return false;
        }

        foreach (var character in line)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<SubtitleCue> ParseAss(string text)
    {
        var inEvents = false;
        var fields = new[] { "layer", "start", "end", "style", "name", "marginl", "marginr", "marginv", "effect", "text" };

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inEvents)
            {
                continue;
            }

            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                fields = line["Format:".Length..]
                    .Split(',', StringSplitOptions.TrimEntries)
                    .Select(field => field.ToLowerInvariant())
                    .ToArray();
                continue;
            }

            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                // Comment: lines are deliberately ignored.
                continue;
            }

            var payload = line["Dialogue:".Length..].TrimStart();
            var parts = payload.Split(',', Math.Max(fields.Length, 2));
            if (parts.Length < fields.Length)
            {
                continue;
            }

            var startIndex = Array.IndexOf(fields, "start");
            var endIndex = Array.IndexOf(fields, "end");
            var textIndex = Array.IndexOf(fields, "text");
            if (startIndex < 0 || endIndex < 0 || textIndex < 0)
            {
                continue;
            }

            if (!TryParseTimestamp(parts[startIndex], out var start) ||
                !TryParseTimestamp(parts[endIndex], out var end))
            {
                continue;
            }

            var cueText = CleanText(parts[textIndex]);
            if (cueText.Length == 0 || end <= start)
            {
                continue;
            }

            yield return new SubtitleCue(start, end, cueText);
        }
    }

    /// <summary>Handles "00:01:02,500 --> 00:01:04,000 align:start" and VTT dot forms.</summary>
    private static bool TryParseTimeRange(string line, out long start, out long end)
    {
        start = 0;
        end = 0;

        var arrow = line.IndexOf("-->", StringComparison.Ordinal);
        if (arrow < 0)
        {
            return false;
        }

        var left = line[..arrow];
        var right = line[(arrow + 3)..];
        return TryParseTimestamp(left, out start) && TryParseTimestamp(right, out end);
    }

    /// <summary>Parses HH:MM:SS,mmm / HH:MM:SS.mmm / H:MM:SS.cc / MM:SS.mmm.</summary>
    public static bool TryParseTimestamp(string raw, out long milliseconds)
    {
        milliseconds = 0;

        var value = raw.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        // Cue settings or trailing style data follow a space.
        var space = value.IndexOfAny([' ', '\t']);
        if (space > 0)
        {
            value = value[..space];
        }

        value = value.Replace(',', '.');
        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        if (!double.TryParse(
                parts[^1],
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var seconds) ||
            !int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
            minutes is < 0 or > 59 ||
            seconds is < 0 or >= 60)
        {
            return false;
        }

        var hours = 0;
        if (parts.Length == 3 &&
            (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours) || hours < 0))
        {
            return false;
        }

        milliseconds = (long)Math.Round(((hours * 60 + minutes) * 60 + seconds) * 1000);
        return milliseconds >= 0;
    }

    /// <summary>Strips markup and turns ASS line breaks into real ones.</summary>
    private static string CleanText(string text)
    {
        var cleaned = AssTagRegex.Replace(text, "");
        cleaned = cleaned
            .Replace("\\N", "\n")
            .Replace("\\n", "\n")
            .Replace("\\h", " ");
        cleaned = HtmlTagRegex.Replace(cleaned, "");
        cleaned = WebUtility.HtmlDecode(cleaned);
        cleaned = cleaned.Replace('\u00A0', ' ').Trim();

        var lines = cleaned
            .Split('\n')
            .Select(line => ExcessSpacesRegex.Replace(line.Trim(), " "))
            .Where(line => line.Length > 0);

        return string.Join('\n', lines);
    }

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            // Subtitle files are overwhelmingly UTF-8; a strict decoder catches
            // the legacy Windows-1252 files that are not.
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}

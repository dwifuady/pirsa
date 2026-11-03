using LibVLCSharp.Shared;

namespace Pirsa.Services;

/// <summary>
/// Append-only playback log for diagnosing buffering and seeking. It records the
/// media options handed to VLC, every seek next to how long VLC actually spent
/// buffering afterwards, and VLC's own stream-layer messages, so "it stalls when
/// I skip" can be answered with numbers instead of guesses.
///
/// The log is local only, capped, and reset every time the player opens, so it
/// never grows without bound and never leaves the machine.
/// </summary>
public static class PlayerLog
{
    private const long MaxBytes = 1_000_000;

    /// <summary>VLC modules whose chatter explains stalls: the stream plumbing.</summary>
    private static readonly HashSet<string> InterestingModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "access", "http", "hls", "adaptive", "mp4", "mkv", "cache",
        "input", "stream", "prefetch", "tls", "xml", "dash",
    };

    private static readonly object Gate = new();
    private static bool _full;
    private static string? _path;

    public static string Path => _path ??= CreatePath();

    /// <summary>Starts a fresh log. Called once, when the player page is created.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _full = false;
            try
            {
                var path = Path;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllText(
                    path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Pirsa player log{Environment.NewLine}" +
                    $"data root: {LocalPaths.Root}{Environment.NewLine}" +
                    $"build: {typeof(PlayerLog).Assembly.GetName().Version}" +
#if DEBUG
                    " DEBUG" +
#endif
                    Environment.NewLine);
            }
            catch
            {
                // Diagnostics must never get in the way of playback.
            }
        }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            if (_full)
            {
                return;
            }

            try
            {
                var path = Path;
                if (new FileInfo(path).Length > MaxBytes)
                {
                    _full = true;
                    File.AppendAllText(path, "-- log full, stopped --" + Environment.NewLine);
                    return;
                }

                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }
    }

    /// <summary>Records one libVLC message, filtered down to the stream plumbing.</summary>
    public static void WriteVlc(LogEventArgs e)
    {
        var module = e.Module ?? "";

        if (e.Level is LogLevel.Error or LogLevel.Warning)
        {
            Write($"vlc {e.Level,-7} {module}: {e.Message}");
            return;
        }

        if (e.Level == LogLevel.Debug && InterestingModules.Contains(module))
        {
            Write($"vlc {e.Level,-7} {module}: {e.Message}");
        }
    }

    private static string CreatePath()
    {
        try
        {
            Directory.CreateDirectory(LocalPaths.Root);
        }
        catch
        {
        }

        return LocalPaths.PlayerLog;
    }
}

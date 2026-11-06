namespace Pirsa.Services;

/// <summary>
/// Append-only playback log for diagnosing buffering and seeking. It records the
/// media options handed to the engine, every seek next to how long the engine
/// actually spent buffering afterwards, and the engine's own stream-layer
/// messages, so "it stalls when I skip" can be answered with numbers instead of
/// guesses.
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

        // Which audio output was created, and with what format. "using audio
        // output module" lives here, and the module that wins owns the buffer
        // of already-played audio that a pause cannot take back.
        "audio output", "mmdevice", "directsound", "waveout", "wasapi",
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

    /// <summary>
    /// Records one engine message, filtered down to the stream plumbing. The
    /// level names are the backend's own (Debug, Notice, Warning, Error).
    /// </summary>
    public static void WriteEngine(string engine, string level, string? module, string message)
    {
        module ??= "";

        if (level.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
            level.Equals("Warning", StringComparison.OrdinalIgnoreCase))
        {
            Write($"{engine} {level,-7} {module}: {message}");
            return;
        }

        if (level.Equals("Debug", StringComparison.OrdinalIgnoreCase) &&
            InterestingModules.Contains(module))
        {
            Write($"{engine} {level,-7} {module}: {message}");
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

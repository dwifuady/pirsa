namespace Pirsa.Services;

/// <summary>
/// Per-user folders that are the same for the installed app and for a
/// development run, so a path quoted in a bug report always means one place:
/// <c>%LOCALAPPDATA%\Pirsa</c>.
///
/// This is deliberately not <c>ApplicationData.Current.LocalFolder</c>. That
/// folder only exists when the process has package identity, so during
/// development the player log and the stream cache would land somewhere else
/// (or fall back to a different root), and "where is the log?" would have a
/// different answer for every way the app is started.
/// </summary>
public static class LocalPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pirsa");

    /// <summary>Playback diagnostics from <see cref="PlayerLog"/>.</summary>
    public static string PlayerLog => Path.Combine(Root, "player.log");

    /// <summary>Read-ahead files from <see cref="StreamCacheSession"/>.</summary>
    public static string StreamCache => Path.Combine(Root, "cache");
}

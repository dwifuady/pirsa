namespace Pirsa.Services;

/// <summary>
/// The one folder Pirsa writes to: settings, the addon list, watch history,
/// watchlist, search history, the player log and the stream cache.
///
/// Resolution is deliberate, because a development session must never touch the
/// data of an installed copy:
///
/// <list type="bullet">
///   <item>A process with package identity (Store install, or a development run
///   started through winapp/<c>dotnet run</c>, which registers a debug identity)
///   gets its own <c>LocalState</c> folder — a different package, a different
///   folder, so the two can never meet.</item>
///   <item>An unpackaged build (the Inno Setup installer, the portable ZIP) uses
///   <c>%LOCALAPPDATA%\Pirsa</c>.</item>
///   <item>An unpackaged <em>Debug</em> build uses <c>%LOCALAPPDATA%\Pirsa-dev</c>,
///   so even a plain debug launch cannot pollute a real install's data.</item>
/// </list>
/// </summary>
public static class LocalPaths
{
    public static string Root { get; } = Resolve();

    /// <summary>Playback diagnostics from <see cref="PlayerLog"/>.</summary>
    public static string PlayerLog => Path.Combine(Root, "player.log");

    /// <summary>Read-ahead files from <see cref="StreamCacheSession"/>.</summary>
    public static string StreamCache => Path.Combine(Root, "cache");

    private static string Resolve()
    {
        try
        {
            var packaged = Windows.Storage.ApplicationData.Current.LocalFolder.Path;

#if DEBUG
            // Belt and braces: a debug identity is already separate, but naming
            // the folder makes it obvious which copy owns the data.
            packaged = Path.Combine(packaged, "dev");
#endif

            return packaged;
        }
        catch
        {
            var name = "Pirsa";
#if DEBUG
            name = "Pirsa-dev";
#endif

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                name);
        }
    }
}

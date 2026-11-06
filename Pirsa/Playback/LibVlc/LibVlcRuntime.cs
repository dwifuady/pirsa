using LibVLCSharp.Shared;

namespace Pirsa.Playback.LibVlc;

/// <summary>
/// Warms the native VLC engine while the user is still browsing. The first
/// LibVLC instance scans plugins and can take a while on a cold install; doing
/// that on a background thread keeps the player page responsive.
/// </summary>
public static class LibVlcRuntime
{
    private static readonly object Gate = new();
    private static Task? _warmUp;

    public static Task WarmUpAsync()
    {
        lock (Gate)
        {
            return _warmUp ??= Task.Run(WarmUp);
        }
    }

    private static void WarmUp()
    {
        try
        {
            Core.Initialize();
            using var vlc = new LibVLC();
        }
        catch
        {
            // The player retries initialization on the UI thread and reports
            // a usable error if the engine still cannot start.
        }
    }
}

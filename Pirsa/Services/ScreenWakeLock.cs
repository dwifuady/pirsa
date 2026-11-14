using System.Runtime.InteropServices;

namespace Pirsa.Services;

/// <summary>
/// Keeps the display and the machine awake while video plays.
///
/// Nothing in the playback path does this for us. Windows only resets the power
/// idle timers for Media Foundation players; both of our backends are native
/// (libVLC and mpv), so the display timeout keeps running while an episode
/// plays. On battery that timeout is short — commonly two to five minutes — so
/// the screen dims and then turns off mid-episode, which reads as the machine
/// going to sleep. On AC the timeout is usually long enough to go unnoticed.
///
/// The state is per-thread, so the lock must be held and released on the UI
/// thread, which lives as long as the window does.
/// </summary>
internal static class ScreenWakeLock
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    private static bool _held;

    /// <summary>
    /// Holds the lock while <paramref name="held"/> is true and releases it
    /// afterwards. Repeats are ignored, so the player can call this on every
    /// transport change.
    /// </summary>
    public static void SetHeld(bool held)
    {
        if (held == _held)
        {
            return;
        }

        var flags = held
            ? EsContinuous | EsSystemRequired | EsDisplayRequired
            : EsContinuous;

        if (SetThreadExecutionState(flags) != 0)
        {
            _held = held;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}

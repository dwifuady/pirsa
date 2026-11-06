using Pirsa.Playback.LibVlc;
using Pirsa.Playback.Mpv;
using Pirsa.Services;

namespace Pirsa.Playback;

/// <summary>
/// The backends this build ships. mpv is preferred when it is available and
/// libVLC is the fallback; Settings stores an id, and an unknown or unavailable
/// id falls back to that same automatic choice.
/// </summary>
public static class PlaybackEngines
{
    private static readonly IReadOnlyList<IPlaybackEngineFactory> Factories =
    [
        new LibVlcPlaybackEngineFactory(),
        new MpvPlaybackEngineFactory(),
    ];

    public static IReadOnlyList<IPlaybackEngineFactory> All => Factories;

    /// <summary>mpv when its native package is present, otherwise libVLC.</summary>
    public static IPlaybackEngineFactory Default =>
        Find("mpv") is { IsAvailable: true } mpv ? mpv : Factories[0];

    public static IPlaybackEngineFactory? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Factories.FirstOrDefault(factory =>
                factory.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The requested backend when it is available, otherwise the default. An
    /// explicit but unavailable choice is logged so a stale settings file is
    /// visible in the player log.
    /// </summary>
    public static IPlaybackEngineFactory Resolve(string? id)
    {
        var requested = Find(id);
        if (requested is { IsAvailable: true })
        {
            return requested;
        }

        if (!string.IsNullOrWhiteSpace(id))
        {
            PlayerLog.Write(requested is null
                ? $"playback engine '{id}' is not installed; using {Default.Id}"
                : $"playback engine '{id}' is unavailable; using {Default.Id}");
        }

        return Default;
    }

    public static IPlaybackEngine Create(string? id = null) => Resolve(id).Create();

    public static Task WarmUpAsync(string? id = null) => Resolve(id).WarmUpAsync();
}

using Pirsa.Playback.LibVlc;

namespace Pirsa.Playback;

/// <summary>
/// The backends this build ships. Only libVLC is registered for now; adding
/// mpv means adding its factory here and, later, reading the user's choice from
/// settings.
/// </summary>
public static class PlaybackEngines
{
    private static readonly IReadOnlyList<IPlaybackEngineFactory> Factories =
    [
        new LibVlcPlaybackEngineFactory(),
    ];

    public static IReadOnlyList<IPlaybackEngineFactory> All => Factories;

    public static IPlaybackEngineFactory Default => Factories[0];

    public static IPlaybackEngineFactory? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Factories.FirstOrDefault(factory =>
                factory.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static IPlaybackEngine Create(string? id = null) =>
        (Find(id) ?? Default).Create();

    public static Task WarmUpAsync(string? id = null) =>
        (Find(id) ?? Default).WarmUpAsync();
}

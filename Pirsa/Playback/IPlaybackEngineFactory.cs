namespace Pirsa.Playback;

/// <summary>
/// Creates backends and warms their native libraries. A factory does not own
/// the page's engine instance; call <see cref="Create"/> once per player page.
/// </summary>
public interface IPlaybackEngineFactory
{
    string Id { get; }

    string DisplayName { get; }

    bool IsAvailable { get; }

    IPlaybackEngine Create();

    Task WarmUpAsync();
}

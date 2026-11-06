namespace Pirsa.Playback.LibVlc;

public sealed class LibVlcPlaybackEngineFactory : IPlaybackEngineFactory
{
    public string Id => "libvlc";

    public string DisplayName => "libVLC";

    public bool IsAvailable => true;

    public IPlaybackEngine Create() => new LibVlcPlaybackEngine();

    public Task WarmUpAsync() => LibVlcRuntime.WarmUpAsync();
}

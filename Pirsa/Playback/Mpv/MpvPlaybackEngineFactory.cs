namespace Pirsa.Playback.Mpv;

public sealed class MpvPlaybackEngineFactory : IPlaybackEngineFactory
{
    public string Id => "mpv";

    public string DisplayName => "mpv";

    public bool IsAvailable => MpvRuntime.IsAvailable;

    public IPlaybackEngine Create() => new MpvPlaybackEngine();

    public Task WarmUpAsync() => MpvRuntime.WarmUpAsync();
}

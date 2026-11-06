namespace Pirsa.Services.Sync;

/// <summary>
/// Everything Settings needs to render a provider row and its connect form,
/// so adding a provider never means adding per-provider XAML.
/// </summary>
public sealed record SyncProviderDescriptor(
    string Id,
    string Name,
    string Description,
    string Coverage,
    SyncCapabilities Capabilities,
    SyncAuthKind AuthKind,
    string ConnectLabel,
    string? Placeholder = null,
    string? Hint = null,
    bool AsksForClientId = false);

/// <summary>
/// The union of the fields a provider might need. Each provider reads only the
/// fields its <see cref="SyncAuthKind"/> describes; the rest stay null.
/// </summary>
public sealed record SyncCredentials(
    string? Url = null,
    string? UserName = null,
    string? Password = null,
    string? ApiKey = null,
    string? ClientId = null,
    string? Token = null,
    string? RefreshToken = null,
    DateTimeOffset? ExpiresAt = null);

public interface ISyncProvider
{
    SyncProviderDescriptor Descriptor { get; }

    /// <summary>
    /// Validates the credentials and returns a session to keep. Used both by
    /// the Connect button and when restoring a saved connection.
    /// </summary>
    /// <param name="prompt">
    /// Device-flow providers report the code to show the user through this.
    /// Providers that need no interaction ignore it.
    /// </param>
    Task<ISyncSession> ConnectAsync(
        SyncCredentials credentials,
        IProgress<SyncAuthPrompt>? prompt = null,
        CancellationToken cancellationToken = default);
}

public interface ISyncSession : IAsyncDisposable
{
    /// <summary>Shown in Settings; must not contain a secret.</summary>
    string AccountLabel { get; }

    /// <summary>Opaque state the engine persists after a successful sync.</summary>
    string? Cursor { get; set; }

    /// <summary>
    /// Set when a token was refreshed or first issued, so the engine can
    /// replace the encrypted credentials it stores.
    /// </summary>
    SyncCredentials? UpdatedCredentials { get; }

    Task<RemoteSnapshot> PullAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the merged local state. <paramref name="remote"/> is what
    /// <see cref="PullAsync"/> returned, so a provider can send a delta when
    /// its API wants one.
    /// </summary>
    Task PushAsync(
        RemoteSnapshot remote,
        SyncSnapshot local,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports a real-time playback event. Only providers with
    /// <see cref="SyncCapabilities.Scrobble"/> receive this.
    /// </summary>
    Task ScrobbleAsync(
        PlaybackSignal signal,
        PlaybackSnapshot snapshot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a paused playback whose local resume row went away. Providers
    /// without server-side playbacks ignore it.
    /// </summary>
    Task RemovePlaybackAsync(PlaybackRef playback, CancellationToken cancellationToken = default);
}

/// <summary>The providers this build knows about. Order is the Settings order.</summary>
public static class SyncProviders
{
    public static IReadOnlyList<ISyncProvider> All { get; } =
    [
        new Providers.SimklSyncProvider(),
        new Providers.MdbListSyncProvider(),
        new Providers.WebDavSyncProvider(),
    ];

    public static IReadOnlyList<SyncProviderDescriptor> Descriptors { get; } =
        [.. All.Select(provider => provider.Descriptor)];

    public static ISyncProvider? Find(string? id) =>
        All.FirstOrDefault(provider =>
            provider.Descriptor.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

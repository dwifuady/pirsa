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
    string? Hint = null);

/// <summary>
/// The union of the fields a provider might need. Each provider reads only the
/// fields its <see cref="SyncAuthKind"/> describes; the rest stay null.
/// </summary>
public sealed record SyncCredentials(
    string? Url = null,
    string? UserName = null,
    string? Password = null,
    string? ApiKey = null,
    string? Token = null);

public interface ISyncProvider
{
    SyncProviderDescriptor Descriptor { get; }

    /// <summary>
    /// Validates the credentials and returns a session to keep. Used both by
    /// the Connect button and when restoring a saved connection.
    /// </summary>
    Task<ISyncSession> ConnectAsync(
        SyncCredentials credentials,
        CancellationToken cancellationToken = default);
}

public interface ISyncSession : IAsyncDisposable
{
    /// <summary>Shown in Settings; must not contain a secret.</summary>
    string AccountLabel { get; }

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
}

/// <summary>The providers this build knows about. Order is the Settings order.</summary>
public static class SyncProviders
{
    public static IReadOnlyList<ISyncProvider> All { get; } =
    [
        new Providers.WebDavSyncProvider(),
    ];

    public static IReadOnlyList<SyncProviderDescriptor> Descriptors { get; } =
        [.. All.Select(provider => provider.Descriptor)];

    public static ISyncProvider? Find(string? id) =>
        All.FirstOrDefault(provider =>
            provider.Descriptor.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

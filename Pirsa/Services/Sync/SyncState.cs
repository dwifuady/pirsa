using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pirsa.Services.Sync;

/// <summary>
/// What survives a restart: which providers are connected, when each last
/// synced, and the tombstones that keep a removal from being resurrected.
/// Secrets live separately in <see cref="SyncStateStore"/>.
/// </summary>
public sealed class SyncState
{
    public List<SyncConnection> Connections { get; set; } = [];

    /// <summary>Timestamped "no longer watched" facts, newest last.</summary>
    public List<WatchEvent> Unwatched { get; set; } = [];

    /// <summary>Timestamped "removed from watchlist" facts, newest last.</summary>
    public List<WatchlistChange> WatchlistRemoved { get; set; } = [];
}

/// <summary>
/// Persists <see cref="SyncState"/> in <c>sync.json</c> and provider secrets in
/// <c>sync-credentials.dat</c>, encrypted with DPAPI for the current user.
/// </summary>
public sealed class SyncStateStore
{
    private const int MaxTombstones = 2000;
    private static readonly TimeSpan TombstoneMaxAge = TimeSpan.FromDays(365);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _loaded;
    private string? _statePath;
    private string? _credentialsPath;

    public SyncState State { get; private set; } = new();

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
        {
            return;
        }

        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
            {
                return;
            }

            try
            {
                var path = StatePath;
                if (File.Exists(path))
                {
                    var state = JsonSerializer.Deserialize<SyncState>(
                        await File.ReadAllTextAsync(path, cancellationToken),
                        JsonOptions);

                    if (state is not null)
                    {
                        State = state;
                        State.Connections ??= [];
                        State.Unwatched ??= [];
                        State.WatchlistRemoved ??= [];
                    }
                }
            }
            catch
            {
                // A corrupt file starts clean; sync reconciles from the providers.
                State = new SyncState();
            }

            TrimTombstones();
            _loaded = true;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            TrimTombstones();
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            await File.WriteAllTextAsync(
                StatePath,
                JsonSerializer.Serialize(State, JsonOptions),
                cancellationToken);
        }
        catch
        {
            // Sync state stays in memory if persistence fails.
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public SyncConnection GetOrAddConnection(string providerId)
    {
        var connection = State.Connections.FirstOrDefault(item =>
            item.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));

        if (connection is null)
        {
            connection = new SyncConnection { ProviderId = providerId };
            State.Connections.Add(connection);
        }

        return connection;
    }

    public void RemoveConnection(string providerId) =>
        State.Connections.RemoveAll(item =>
            item.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));

    public void RecordUnwatched(WatchEvent tombstone)
    {
        State.Unwatched.RemoveAll(item =>
            item.Key.Equals(tombstone.Key, StringComparison.OrdinalIgnoreCase));
        State.Unwatched.Add(tombstone);
        TrimTombstones();
    }

    public void RecordWatchlistRemoved(WatchlistChange tombstone)
    {
        State.WatchlistRemoved.RemoveAll(item =>
            item.Key.Equals(tombstone.Key, StringComparison.OrdinalIgnoreCase));
        State.WatchlistRemoved.Add(tombstone);
        TrimTombstones();
    }

    public async Task<SyncCredentials?> LoadCredentialsAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var secrets = await LoadSecretsAsync(cancellationToken);
        if (!secrets.TryGetValue(providerId, out var protectedValue))
        {
            return null;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedValue),
                null,
                DataProtectionScope.CurrentUser);

            return JsonSerializer.Deserialize<SyncCredentials>(bytes, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveCredentialsAsync(
        string providerId,
        SyncCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var secrets = await LoadSecretsAsync(cancellationToken);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(credentials, JsonOptions);
        secrets[providerId] = Convert.ToBase64String(
            ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));

        await SaveSecretsAsync(secrets, cancellationToken);
    }

    public async Task RemoveCredentialsAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var secrets = await LoadSecretsAsync(cancellationToken);
        if (secrets.Remove(providerId))
        {
            await SaveSecretsAsync(secrets, cancellationToken);
        }
    }

    private async Task<Dictionary<string, string>> LoadSecretsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(CredentialsPath))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var json = await File.ReadAllTextAsync(CredentialsPath, cancellationToken);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task SaveSecretsAsync(
        Dictionary<string, string> secrets,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CredentialsPath)!);
        await File.WriteAllTextAsync(
            CredentialsPath,
            JsonSerializer.Serialize(secrets),
            cancellationToken);
    }

    private void TrimTombstones()
    {
        var cutoff = DateTimeOffset.UtcNow - TombstoneMaxAge;

        State.Unwatched = [.. State.Unwatched
            .Where(item => item.At >= cutoff)
            .OrderBy(item => item.At)
            .TakeLast(MaxTombstones)];

        State.WatchlistRemoved = [.. State.WatchlistRemoved
            .Where(item => item.At >= cutoff)
            .OrderBy(item => item.At)
            .TakeLast(MaxTombstones)];
    }

    private string StatePath => _statePath ??= Path.Combine(LocalPaths.Root, "sync.json");

    private string CredentialsPath =>
        _credentialsPath ??= Path.Combine(LocalPaths.Root, "sync-credentials.dat");
}

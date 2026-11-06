using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Pirsa.Services;
using Pirsa.Services.Sync;

namespace Pirsa.ViewModels;

/// <summary>
/// One provider card in Settings → Sync. Holds both the connected state and
/// the form fields, so the page needs no per-provider controls.
/// </summary>
public sealed partial class SyncProviderRowViewModel : ObservableObject
{
    public SyncProviderRowViewModel(SyncProviderDescriptor descriptor)
    {
        Descriptor = descriptor;
        Url = "";
        UserName = "";
        Password = "";
        AccountLabel = "";
        StatusText = "";
        ErrorMessage = "";
    }

    public SyncProviderDescriptor Descriptor { get; }

    public string Id => Descriptor.Id;

    public string Name => Descriptor.Name;

    public string Description => Descriptor.Description;

    public string Coverage => Descriptor.Coverage;

    public string ConnectLabel => Descriptor.ConnectLabel;

    public string? Placeholder => Descriptor.Placeholder;

    public string? Hint => Descriptor.Hint;

    public bool UsesUrl => Descriptor.AuthKind == SyncAuthKind.UrlWithPassword;

    /// <summary>Connect form state, never persisted.</summary>
    [ObservableProperty]
    public partial string Url { get; set; }

    [ObservableProperty]
    public partial string UserName { get; set; }

    [ObservableProperty]
    public partial string Password { get; set; }

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial string AccountLabel { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; }
}

/// <summary>
/// Settings → Sync. Lists every provider the build knows about, connects one
/// with the fields its descriptor asks for, and reports the last sync state.
/// Several providers can be connected at once; each is an independent mirror.
/// </summary>
public sealed partial class SyncSettingsViewModel : ObservableObject
{
    private readonly SyncEngine _sync;
    private readonly EventHandler _changedHandler;

    public SyncSettingsViewModel(SyncEngine sync)
    {
        _sync = sync;
        IsBusy = false;
        SetStatus("");

        foreach (var descriptor in sync.Providers)
        {
            Providers.Add(new SyncProviderRowViewModel(descriptor));
        }

        _changedHandler = (_, _) => Refresh();
        _sync.Changed += _changedHandler;
        RefreshCore();
    }

    public ObservableCollection<SyncProviderRowViewModel> Providers { get; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasStatus { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    public bool HasConnections => _sync.Connections.Count > 0;

    public string ConnectedCountText => _sync.Connections.Count switch
    {
        0 => "No service connected",
        1 => "1 service connected",
        var count => $"{count} services connected",
    };

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _sync.StartAsync(cancellationToken);
        RefreshCore();
    }

    public void Detach()
    {
        _sync.Changed -= _changedHandler;
    }

    public async Task ConnectAsync(
        SyncProviderRowViewModel row,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        if (row.Descriptor.AuthKind != SyncAuthKind.UrlWithPassword)
        {
            SetError($"{row.Name} needs a sign-in flow that is not available yet.");
            return;
        }

        IsBusy = true;
        SetStatus("");

        try
        {
            var result = await _sync.ConnectAsync(
                row.Id,
                new SyncCredentials(
                    Url: row.Url.Trim(),
                    UserName: row.UserName.Trim(),
                    Password: row.Password),
                cancellationToken);

            if (result.Success)
            {
                row.Password = "";
                SetStatus(result.Message);
            }
            else
            {
                SetError(result.Message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
        finally
        {
            IsBusy = false;
            RefreshCore();
        }
    }

    public async Task DisconnectAsync(
        SyncProviderRowViewModel row,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        SetStatus("");

        try
        {
            await _sync.DisconnectAsync(row.Id, cancellationToken);
            SetStatus($"{row.Name} disconnected. Local history stays on this device.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
        finally
        {
            IsBusy = false;
            RefreshCore();
        }
    }

    public async Task SyncNowAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        SetStatus("");

        try
        {
            var result = await _sync.SyncNowAsync(cancellationToken);
            if (result.Success)
            {
                SetStatus(result.Message);
            }
            else
            {
                SetError(result.Message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
        finally
        {
            IsBusy = false;
            RefreshCore();
        }
    }

    private void Refresh()
    {
        if (App.DispatcherQueue is { } queue && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(RefreshCore);
            return;
        }

        RefreshCore();
    }

    private void RefreshCore()
    {
        foreach (var row in Providers)
        {
            var connection = _sync.FindConnection(row.Id);

            row.IsConnected = connection is not null;
            row.AccountLabel = connection?.AccountLabel ?? "";
            row.HasError = connection is { LastError.Length: > 0 };
            row.ErrorMessage = connection?.LastError ?? "";
            row.StatusText = connection switch
            {
                null => "",
                { LastSyncedAt: { } at } => $"Last synced {Relative(at)}",
                _ => "Not synced yet",
            };
        }

        OnPropertyChanged(nameof(HasConnections));
        OnPropertyChanged(nameof(ConnectedCountText));
    }

    private void SetStatus(string message)
    {
        StatusMessage = message;
        HasStatus = !string.IsNullOrWhiteSpace(message);
        HasError = false;
        ErrorMessage = "";
    }

    private void SetError(string message)
    {
        ErrorMessage = message;
        HasError = !string.IsNullOrWhiteSpace(message);
        HasStatus = false;
        StatusMessage = "";
    }

    private static string Relative(DateTimeOffset at)
    {
        var delta = DateTimeOffset.UtcNow - at;

        if (delta < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            return $"{(int)delta.TotalMinutes} min ago";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            return $"{(int)delta.TotalHours} h ago";
        }

        return at.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture);
    }
}

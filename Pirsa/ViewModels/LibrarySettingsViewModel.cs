using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Pirsa.Services;

namespace Pirsa.ViewModels;

/// <summary>
/// Settings → Library &amp; data. Local history, saved searches, the Home row
/// summary and the folder that holds all of it.
/// </summary>
public partial class LibrarySettingsViewModel : ObservableObject
{
    private readonly AddonRegistry _registry;
    private readonly WatchProgressStore _watchProgress;
    private readonly SearchHistoryStore _searchHistory;
    private readonly EventHandler _registryChangedHandler;
    private readonly EventHandler _historyChangedHandler;

    public LibrarySettingsViewModel(
        AddonRegistry registry,
        WatchProgressStore watchProgress,
        SearchHistoryStore searchHistory)
    {
        _registry = registry;
        _watchProgress = watchProgress;
        _searchHistory = searchHistory;

        _registryChangedHandler = (_, _) => OnUi(() => OnPropertyChanged(nameof(HomeSummary)));
        _historyChangedHandler = (_, _) => OnUi(RaiseHistoryInfo);
    }

    /// <summary>Subscribes to the shared stores while the page is open.</summary>
    public void Attach()
    {
        _registry.Changed -= _registryChangedHandler;
        _watchProgress.Changed -= _historyChangedHandler;
        _searchHistory.Changed -= _historyChangedHandler;
        _registry.Changed += _registryChangedHandler;
        _watchProgress.Changed += _historyChangedHandler;
        _searchHistory.Changed += _historyChangedHandler;
    }

    public void Detach()
    {
        _registry.Changed -= _registryChangedHandler;
        _watchProgress.Changed -= _historyChangedHandler;
        _searchHistory.Changed -= _historyChangedHandler;
    }

    public string HomeSummary => _registry.HomeSummary;

    public bool HasWatchHistory => _watchProgress.Entries.Count > 0;

    public string WatchHistoryText => _watchProgress.Entries.Count switch
    {
        0 => "No saved playback positions.",
        1 => "1 saved playback position on this device.",
        var count => $"{count} saved playback positions on this device.",
    };

    public bool HasSearchHistory => _searchHistory.Entries.Count > 0;

    public string SearchHistoryText => _searchHistory.Entries.Count switch
    {
        0 => "No saved searches.",
        1 => "1 saved search on this device.",
        var count => $"{count} saved searches on this device.",
    };

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasNotice { get; set; }

    [ObservableProperty]
    public partial string NoticeMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = "";

    [ObservableProperty]
    public partial string CacheSizeText { get; set; } = "0 KB";

    [ObservableProperty]
    public partial string LogSizeText { get; set; } = "0 KB";

    [ObservableProperty]
    public partial bool HasCache { get; set; }

    public bool CanClearCache => HasCache && !IsBusy;

    partial void OnHasCacheChanged(bool value) => OnPropertyChanged(nameof(CanClearCache));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanClearCache));

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _registry.EnsureLoadedAsync(cancellationToken);
        await _watchProgress.EnsureLoadedAsync(cancellationToken);
        await _searchHistory.EnsureLoadedAsync(cancellationToken);
        OnPropertyChanged(nameof(HomeSummary));
        RaiseHistoryInfo();
        await RefreshStorageAsync(cancellationToken);
    }

    public async Task RefreshStorageAsync(CancellationToken cancellationToken = default)
    {
        var cache = await Task.Run(
            () => StorageUsage.SizeOf(LocalPaths.StreamCache),
            cancellationToken);
        var log = await Task.Run(
            () => StorageUsage.SizeOf(LocalPaths.PlayerLog),
            cancellationToken);

        CacheSizeText = StorageUsage.Format(cache);
        LogSizeText = StorageUsage.Format(log);
        HasCache = cache > 0;
    }

    public async Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await Task.Run(
                () => StorageUsage.ClearDirectory(LocalPaths.StreamCache),
                cancellationToken);
            await RefreshStorageAsync(cancellationToken);
            SetNotice("Stream cache cleared.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError($"Couldn't clear the stream cache. {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void OpenPlayerLog()
    {
        try
        {
            if (!File.Exists(LocalPaths.PlayerLog))
            {
                SetNotice("The player log appears after the first playback.");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = LocalPaths.PlayerLog,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            SetError($"Couldn't open the player log. {exception.Message}");
        }
    }

    public async Task ResetAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await App.Settings.ResetToDefaultsAsync(cancellationToken);
            if (App.Window is MainWindow window)
            {
                window.ApplyTheme();
            }

            SetNotice("All settings were reset. Add-ons and history are unchanged.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError($"Couldn't reset the settings. {exception.Message}");
        }
    }

    public async Task ClearWatchHistoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _watchProgress.ClearAsync(cancellationToken);
            await App.SubtitleOffsets.ClearAsync(cancellationToken);
            await App.SubtitleChoices.ClearAsync(cancellationToken);
            RaiseHistoryInfo();
            SetNotice("Watch history cleared.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError($"Couldn't clear the watch history. {exception.Message}");
        }
    }

    public async Task ClearSearchHistoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _searchHistory.ClearAsync(cancellationToken);
            RaiseHistoryInfo();
            SetNotice("Search history cleared.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError($"Couldn't clear the search history. {exception.Message}");
        }
    }

    public void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LocalPaths.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            SetError($"Couldn't open the data folder. {exception.Message}");
        }
    }

    public void DismissNotice() => HasNotice = false;

    private void RaiseHistoryInfo()
    {
        OnPropertyChanged(nameof(HasWatchHistory));
        OnPropertyChanged(nameof(WatchHistoryText));
        OnPropertyChanged(nameof(HasSearchHistory));
        OnPropertyChanged(nameof(SearchHistoryText));
    }

    private void SetNotice(string message)
    {
        NoticeMessage = message;
        HasNotice = true;
        HasError = false;
        ErrorMessage = "";
    }

    private void SetError(string message)
    {
        ErrorMessage = message;
        HasError = true;
        HasNotice = false;
        NoticeMessage = "";
    }

    private static void OnUi(Action action)
    {
        var queue = App.DispatcherQueue;
        if (queue is null || queue.HasThreadAccess)
        {
            action();
            return;
        }

        queue.TryEnqueue(() => action());
    }
}

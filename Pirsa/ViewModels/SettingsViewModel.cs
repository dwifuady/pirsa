using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pirsa.Services;

namespace Pirsa.ViewModels;

/// <summary>
/// Settings → Addons. Adds and removes persisted Stremio manifest URLs.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AddonRegistry _registry;
    private readonly AppSettings _settings;
    private bool _loadingSettings;

    public SettingsViewModel(AddonRegistry registry, AppSettings settings)
    {
        _registry = registry;
        _settings = settings;
        NewManifestUrl = "";
        ErrorMessage = "";
        _loadingSettings = true;
        AutoplayEnabled = false;
        QualityIndex = 0;
        _loadingSettings = false;
        _registry.Entries.CollectionChanged += (_, _) => RaiseAddonInfo();
    }

    public ObservableCollection<AddonEntry> Addons => _registry.Entries;

    [ObservableProperty]
    public partial string NewManifestUrl { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool AutoplayEnabled { get; set; }

    /// <summary>0 = best, 1 = 4K, 2 = 1440p, 3 = 1080p, 4 = 720p, 5 = 480p.</summary>
    [ObservableProperty]
    public partial int QualityIndex { get; set; }

    public static readonly string[] QualityCodes = ["", "4K", "1440p", "1080p", "720p", "480p"];

    public bool IsEmpty => Addons.Count == 0;

    public bool HasAddons => Addons.Count > 0;

    public string AddonCountText => Addons.Count switch
    {
        0 => "None configured",
        1 => "1 addon",
        _ => $"{Addons.Count} addons",
    };

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _registry.EnsureLoadedAsync(cancellationToken);
            RaiseAddonInfo();
            await _settings.EnsureLoadedAsync(cancellationToken);

            _loadingSettings = true;
            AutoplayEnabled = _settings.Autoplay;
            var qualityIndex = Array.IndexOf(QualityCodes, _settings.PreferredQuality ?? "");
            QualityIndex = qualityIndex < 0 ? 0 : qualityIndex;
            _loadingSettings = false;

            await _registry.LoadManifestsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task AddAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(NewManifestUrl))
        {
            SetError("Paste a Stremio addon manifest URL first.");
            return;
        }

        IsBusy = true;
        HasError = false;
        ErrorMessage = "";

        try
        {
            await _registry.AddAsync(NewManifestUrl, cancellationToken);
            NewManifestUrl = "";
            RaiseAddonInfo();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (StremioException exception)
        {
            SetError(exception.Message);
        }
        catch (Exception exception)
        {
            SetError($"Couldn't add that addon. {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RemoveAsync(AddonEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            await _registry.RemoveAsync(entry, cancellationToken);
            RaiseAddonInfo();
        }
        catch (Exception exception)
        {
            SetError($"Couldn't remove that addon. {exception.Message}");
        }
    }

    public async Task RestoreDefaultsAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        HasError = false;
        try
        {
            await _registry.RestoreDefaultsAsync(cancellationToken);
            RaiseAddonInfo();
        }
        catch (Exception exception)
        {
            SetError($"Couldn't restore the default addon. {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RetryAsync(AddonEntry entry, CancellationToken cancellationToken = default)
    {
        await entry.LoadManifestAsync(cancellationToken);
        RaiseAddonInfo();
    }

    partial void OnAutoplayEnabledChanged(bool value)
    {
        if (!_loadingSettings)
        {
            _settings.Autoplay = value;
            _ = _settings.SaveAsync();
        }
    }

    partial void OnQualityIndexChanged(int value)
    {
        if (!_loadingSettings)
        {
            _settings.PreferredQuality = value >= 0 && value < QualityCodes.Length
                ? QualityCodes[value]
                : "";
            _ = _settings.SaveAsync();
        }
    }

    private void SetError(string message)
    {
        HasError = true;
        ErrorMessage = message;
    }

    private void RaiseAddonInfo()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasAddons));
        OnPropertyChanged(nameof(AddonCountText));
    }
}

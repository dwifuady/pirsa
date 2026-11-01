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
        PatternError = "";
        _loadingSettings = true;
        AutoplayIndex = 0;
        AutoplayPattern = "";
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

    /// <summary>0 = ask, 1 = first source, 2 = pattern.</summary>
    [ObservableProperty]
    public partial int AutoplayIndex { get; set; }

    [ObservableProperty]
    public partial string AutoplayPattern { get; set; }

    [ObservableProperty]
    public partial string PatternError { get; set; }

    public bool IsPatternMode => AutoplayIndex == 2;

    public bool HasPatternError => IsPatternMode && !string.IsNullOrWhiteSpace(PatternError);

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
            AutoplayIndex = _settings.AutoplayMode switch
            {
                AutoplayMode.FirstAvailable => 1,
                AutoplayMode.Pattern => 2,
                _ => 0,
            };
            AutoplayPattern = _settings.AutoplayPattern;
            _loadingSettings = false;
            ValidatePattern();

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

    partial void OnAutoplayIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsPatternMode));
        OnPropertyChanged(nameof(HasPatternError));

        if (!_loadingSettings)
        {
            _settings.AutoplayMode = value switch
            {
                1 => AutoplayMode.FirstAvailable,
                2 => AutoplayMode.Pattern,
                _ => AutoplayMode.Ask,
            };
            _ = _settings.SaveAsync();
        }

        ValidatePattern();
    }

    partial void OnAutoplayPatternChanged(string value)
    {
        if (!_loadingSettings)
        {
            _settings.AutoplayPattern = value ?? "";
            _ = _settings.SaveAsync();
        }

        ValidatePattern();
    }

    private void ValidatePattern()
    {
        if (!IsPatternMode)
        {
            PatternError = "";
        }
        else if (!AppSettings.TryValidatePattern(AutoplayPattern, out var error))
        {
            PatternError = error;
        }
        else
        {
            PatternError = "";
        }

        OnPropertyChanged(nameof(HasPatternError));
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

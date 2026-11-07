using CommunityToolkit.Mvvm.ComponentModel;
using Pirsa.Helpers;
using Pirsa.Services;

namespace Pirsa.ViewModels;

/// <summary>
/// Settings → General. Appearance, the start page and update checks. Theme and
/// start page are app-wide; they are stored in settings.json like the rest.
/// </summary>
public partial class GeneralSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private bool _loadingSettings;

    public GeneralSettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        _loadingSettings = true;
        ThemeIndex = 0;
        StartPageIndex = 0;
        CheckForUpdates = true;
        _loadingSettings = false;
    }

    public string AppVersion => AppInfo.VersionText;

    /// <summary>0 = System, 1 = Light, 2 = Dark.</summary>
    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    /// <summary>0 = Home, 1 = Discover, 2 = Watchlist. Applied next launch.</summary>
    [ObservableProperty]
    public partial int StartPageIndex { get; set; }

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = "";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _settings.EnsureLoadedAsync(cancellationToken);

        _loadingSettings = true;
        ThemeIndex = _settings.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };
        StartPageIndex = _settings.StartPage switch
        {
            "Discover" => 1,
            "Watchlist" => 2,
            _ => 0,
        };
        CheckForUpdates = _settings.CheckForUpdates;
        _loadingSettings = false;
    }

    public async Task CheckNowAsync()
    {
        UpdateStatus = "Checking…";
        try
        {
            if (App.Window is MainWindow window)
            {
                await window.Update.CheckNowAsync();
                UpdateStatus = window.Update.IsOpen
                    ? "An update is available. See the notice."
                    : "Checked just now.";
            }
        }
        catch (Exception exception)
        {
            UpdateStatus = $"Couldn't check for updates. {exception.Message}";
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.Theme = value switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System",
        };
        _ = _settings.SaveAsync();

        if (App.Window is MainWindow window)
        {
            window.ApplyTheme();
        }
    }

    partial void OnStartPageIndexChanged(int value)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.StartPage = value switch
        {
            1 => "Discover",
            2 => "Watchlist",
            _ => "Home",
        };
        _ = _settings.SaveAsync();
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.CheckForUpdates = value;
        _ = _settings.SaveAsync();
    }
}

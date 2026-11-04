using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pirsa.Helpers;
using Pirsa.Services;

namespace Pirsa.ViewModels;

/// <summary>
/// Settings → Addons. Adds, removes, enables, and orders persisted Stremio
/// manifest URLs. The list order is the addon priority order used by metadata
/// and stream discovery.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    /// <summary>Above this many addons, searching and filtering appear.</summary>
    private const int SearchThreshold = 5;

    private readonly AddonRegistry _registry;
    private readonly AppSettings _settings;
    private readonly WatchProgressStore _watchProgress;
    private readonly SearchHistoryStore _searchHistory;
    private readonly HashSet<AddonEntry> _observed = [];
    private readonly EventHandler _registryChangedHandler;
    private readonly NotifyCollectionChangedEventHandler _entriesChangedHandler;
    private bool _loadingSettings;
    private bool _loadingLinkRegistration;
    private bool _rebuilding;

    public SettingsViewModel(
        AddonRegistry registry,
        AppSettings settings,
        WatchProgressStore watchProgress,
        SearchHistoryStore searchHistory)
    {
        _registry = registry;
        _settings = settings;
        _watchProgress = watchProgress;
        _searchHistory = searchHistory;

        // Off first, then every language the subtitle picker understands.
        SubtitleLanguageOptions.Add(new SubtitleLanguageOption("", "Off"));
        foreach (var option in SubtitleLanguages.PreferenceOptions)
        {
            SubtitleLanguageOptions.Add(option);
        }

        NewManifestUrl = "";
        SearchText = "";
        ErrorMessage = "";
        NoticeMessage = "";
        _loadingSettings = true;
        AutoplayEnabled = false;
        QualityIndex = 0;
        SelectedSubtitleLanguage = SubtitleLanguageOptions[0];
        _loadingSettings = false;

        _registryChangedHandler = (_, _) =>
        {
            ObserveEntries();
            RaiseAddonInfo();
            RebuildVisible();
        };

        _entriesChangedHandler = (_, _) => ObserveEntries();

        Attach();
    }

    /// <summary>Subscribes to the shared addon list (also after a cached navigation).</summary>
    public void Attach()
    {
        _registry.Changed -= _registryChangedHandler;
        _registry.Entries.CollectionChanged -= _entriesChangedHandler;
        _registry.Changed += _registryChangedHandler;
        _registry.Entries.CollectionChanged += _entriesChangedHandler;
    }

    /// <summary>Stops listening to the shared addon list when the page goes away.</summary>
    public void Detach()
    {
        _registry.Changed -= _registryChangedHandler;
        _registry.Entries.CollectionChanged -= _entriesChangedHandler;

        foreach (var entry in _observed)
        {
            entry.PropertyChanged -= Entry_PropertyChanged;
        }

        _observed.Clear();
    }

    public ObservableCollection<AddonEntry> Addons => _registry.Entries;

    /// <summary>The rows shown after applying the search text and filter.</summary>
    public ObservableCollection<AddonEntry> VisibleAddons { get; } = [];

    /// <summary>Short pinned-catalogs summary for the Home settings card.</summary>
    public string HomeSummary => _registry.HomeSummary;

    [ObservableProperty]
    public partial string NewManifestUrl { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; }

    /// <summary>0 = all, 1 = streams, 2 = metadata, 3 = catalogs, 4 = issues.</summary>
    [ObservableProperty]
    public partial int FilterIndex { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool HasNotice { get; set; }

    [ObservableProperty]
    public partial string NoticeMessage { get; set; }

    [ObservableProperty]
    public partial bool AutoplayEnabled { get; set; }

    /// <summary>When true, the next episode starts when the current one ends.</summary>
    [ObservableProperty]
    public partial bool AutoAdvanceEnabled { get; set; }

    /// <summary>0 = best, 1 = 4K, 2 = 1440p, 3 = 1080p, 4 = 720p, 5 = 480p.</summary>
    [ObservableProperty]
    public partial int QualityIndex { get; set; }

    /// <summary>Off plus every language subtitles can turn on automatically.</summary>
    public ObservableCollection<SubtitleLanguageOption> SubtitleLanguageOptions { get; } = [];

    [ObservableProperty]
    public partial SubtitleLanguageOption? SelectedSubtitleLanguage { get; set; }

    /// <summary>True for the Store/winapp build, which registers links in its manifest.</summary>
    public bool IsPackagedBuild => StremioProtocolRegistration.IsPackaged;

    /// <summary>Only an unpackaged copy can be registered from Settings.</summary>
    public bool CanManageLinkRegistration => !IsPackagedBuild;

    /// <summary>Whether this copy currently handles <c>stremio://</c> links.</summary>
    [ObservableProperty]
    public partial bool HandlesStremioLinks { get; set; }

    partial void OnHandlesStremioLinksChanged(bool value)
    {
        if (_loadingLinkRegistration || !CanManageLinkRegistration)
        {
            return;
        }

        try
        {
            if (value)
            {
                StremioProtocolRegistration.Register();
            }
            else
            {
                StremioProtocolRegistration.Unregister();
            }

            HasError = false;
            ErrorMessage = "";
            SetNotice(value
                ? "Pirsa now opens stremio:// addon links."
                : "Pirsa no longer opens stremio:// addon links.");
        }
        catch (Exception exception)
        {
            _loadingLinkRegistration = true;
            HandlesStremioLinks = !value;
            _loadingLinkRegistration = false;
            SetError($"Couldn't update stremio:// link handling. {exception.Message}");
        }
    }

    public static readonly string[] QualityCodes = ["", "4K", "1440p", "1080p", "720p", "480p"];

    public bool IsEmpty => Addons.Count == 0;

    public bool HasWatchHistory => _watchProgress.Entries.Count > 0;

    public string WatchHistoryText => _watchProgress.Entries.Count switch
    {
        0 => "No saved playback positions on this device.",
        1 => "1 saved playback position on this device.",
        var count => $"{count} saved playback positions on this device.",
    };

    public bool HasSearchHistory => _searchHistory.Entries.Count > 0;

    public string SearchHistoryText => _searchHistory.Entries.Count switch
    {
        0 => "No saved searches on this device.",
        1 => "1 saved search on this device.",
        var count => $"{count} saved searches on this device.",
    };

    public bool HasAddons => Addons.Count > 0;

    /// <summary>Search and filters are only worth the space with a long list.</summary>
    public bool HasManyAddons => Addons.Count > SearchThreshold;

    public bool IsFiltered => HasManyAddons &&
        (FilterIndex != 0 || !string.IsNullOrWhiteSpace(SearchText));

    public bool CanReorder => !IsFiltered;

    public bool HasNoMatches => HasAddons && VisibleAddons.Count == 0;

    public string AddonCountText
    {
        get
        {
            if (Addons.Count == 0)
            {
                return "None configured";
            }

            var disabled = Addons.Count(entry => !entry.IsEnabled);
            var total = Addons.Count == 1 ? "1 addon" : $"{Addons.Count} addons";
            return disabled == 0 ? total : $"{total} · {disabled} disabled";
        }
    }

    public string VisibleCountText => IsFiltered
        ? $"Showing {VisibleAddons.Count} of {Addons.Count}"
        : "";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _registry.EnsureLoadedAsync(cancellationToken);
            ObserveEntries();
            RaiseAddonInfo();
            RebuildVisible();

            await _settings.EnsureLoadedAsync(cancellationToken);
            await _watchProgress.EnsureLoadedAsync(cancellationToken);
            RaiseWatchHistoryInfo();
            await _searchHistory.EnsureLoadedAsync(cancellationToken);
            RaiseSearchHistoryInfo();

            _loadingLinkRegistration = true;
            HandlesStremioLinks = CanManageLinkRegistration && StremioProtocolRegistration.IsRegistered;
            _loadingLinkRegistration = false;

            _loadingSettings = true;
            AutoplayEnabled = _settings.Autoplay;
            AutoAdvanceEnabled = _settings.AutoAdvance;
            SelectedSubtitleLanguage = ResolveSubtitleLanguage(_settings.PreferredSubtitleLanguage);
            var qualityIndex = Array.IndexOf(QualityCodes, _settings.PreferredQuality ?? "");
            QualityIndex = qualityIndex < 0 ? 0 : qualityIndex;
            _loadingSettings = false;

            await _registry.LoadManifestsAsync(cancellationToken, includeDisabled: true);
            OnPropertyChanged(nameof(HomeSummary));
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
            var wasPreferredMeta =
                _registry.PreferredMetaEntry?.ManifestUrl == entry.ManifestUrl;

            await _registry.RemoveAsync(entry, cancellationToken);
            RaiseAddonInfo();

            if (wasPreferredMeta)
            {
                var successor = _registry.PreferredMetaEntry;
                SetNotice(successor is null
                    ? $"{entry.Name} was your preferred metadata addon. No addon provides metadata now."
                    : $"{entry.Name} was your preferred metadata addon. {successor.Name} now provides details.");
            }
        }
        catch (Exception exception)
        {
            SetError($"Couldn't remove that addon. {exception.Message}");
        }
    }

    /// <summary>Moves an addon one row up (-1) or down (+1). Disabled while filtered.</summary>
    public async Task MoveAsync(AddonEntry entry, int offset, CancellationToken cancellationToken = default)
    {
        if (!CanReorder)
        {
            return;
        }

        try
        {
            await _registry.MoveAsync(entry, offset, cancellationToken);
        }
        catch (Exception exception)
        {
            SetError($"Couldn't change the addon order. {exception.Message}");
        }
    }

    /// <summary>
    /// Enables or disables one addon. Disabling the preferred metadata addon
    /// announces which addon takes over, since that changes the details page.
    /// </summary>
    public async Task SetEnabledAsync(
        AddonEntry entry,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (entry.IsEnabled == enabled)
        {
            return;
        }

        try
        {
            var wasPreferredMeta =
                _registry.PreferredMetaEntry?.ManifestUrl == entry.ManifestUrl;

            entry.IsEnabled = enabled;
            await _registry.EnsureLoadedAsync(cancellationToken);

            if (wasPreferredMeta && !enabled)
            {
                var successor = _registry.PreferredMetaEntry;
                SetNotice(successor is null
                    ? $"{entry.Name} is disabled. No addon provides metadata now."
                    : $"{entry.Name} was your preferred metadata addon. {successor.Name} now provides details.");
            }

            RaiseAddonInfo();
            RebuildVisible();
        }
        catch (Exception exception)
        {
            SetError($"Couldn't change that addon. {exception.Message}");
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
            SetNotice("Cinemeta and OpenSubtitles v3 were restored in the default order.");
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
        RebuildVisible();
        OnPropertyChanged(nameof(HomeSummary));
    }

    public void ClearFilters()
    {
        SearchText = "";
        FilterIndex = 0;
    }

    public void DismissNotice() => HasNotice = false;

    /// <summary>Shows the same informational bar the addon page uses.</summary>
    public void ShowNotice(string message) => SetNotice(message);

    /// <summary>Shows the same error bar the addon page uses.</summary>
    public void ShowError(string message) => SetError(message);

    public async Task ClearWatchHistoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _watchProgress.ClearAsync(cancellationToken);
            await App.SubtitleOffsets.ClearAsync(cancellationToken);
            RaiseWatchHistoryInfo();
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
            RaiseSearchHistoryInfo();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError($"Couldn't clear the search history. {exception.Message}");
        }
    }

    partial void OnAutoplayEnabledChanged(bool value)
    {
        if (!_loadingSettings)
        {
            _settings.Autoplay = value;
            _ = _settings.SaveAsync();
        }
    }

    partial void OnAutoAdvanceEnabledChanged(bool value)
    {
        if (!_loadingSettings)
        {
            _settings.AutoAdvance = value;
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

    partial void OnSelectedSubtitleLanguageChanged(SubtitleLanguageOption? value)
    {
        if (!_loadingSettings)
        {
            _settings.PreferredSubtitleLanguage = value?.Code ?? "";
            _ = _settings.SaveAsync();
        }
    }

    partial void OnSearchTextChanged(string value) => RebuildVisible();

    partial void OnFilterIndexChanged(int value) => RebuildVisible();

    /// <summary>
    /// Finds the picker row for a stored code (eng and en are the same row). A
    /// code no title offered yet is added so the choice is never silently lost.
    /// </summary>
    private SubtitleLanguageOption ResolveSubtitleLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return SubtitleLanguageOptions[0];
        }

        if (SubtitleLanguageOptions.FirstOrDefault(option =>
                !option.IsOff && SubtitleLanguages.SameLanguage(option.Code, code)) is { } match)
        {
            return match;
        }

        var extra = new SubtitleLanguageOption(
            SubtitleLanguages.CanonicalCode(code),
            SubtitleLanguages.DisplayName(code));
        SubtitleLanguageOptions.Add(extra);
        return extra;
    }

    private void RebuildVisible()
    {
        if (_rebuilding)
        {
            return;
        }

        _rebuilding = true;
        try
        {
            var query = SearchText?.Trim() ?? "";
            var canReorder = CanReorder;
            var visible = new List<AddonEntry>(Addons.Count);

            foreach (var entry in Addons)
            {
                entry.CanReorder = canReorder;
                entry.CanMoveUp = canReorder && entry.Order > 1;
                entry.CanMoveDown = canReorder && entry.Order < Addons.Count;

                if (Matches(entry, query))
                {
                    visible.Add(entry);
                }
            }

            // Keep the containers when only a property changed, so the toggle
            // that raised this refresh keeps keyboard focus.
            if (!VisibleAddons.SequenceEqual(visible))
            {
                VisibleAddons.Clear();
                foreach (var entry in visible)
                {
                    VisibleAddons.Add(entry);
                }
            }
        }
        finally
        {
            _rebuilding = false;
        }

        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(CanReorder));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(VisibleCountText));
    }

    private bool Matches(AddonEntry entry, string query)
    {
        // With a short list the toolbar is hidden, so it must not filter.
        if (!HasManyAddons)
        {
            return true;
        }

        if (query.Length > 0 &&
            !entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !entry.Host.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return FilterIndex switch
        {
            1 => entry.HasStreams,
            2 => entry.HasMeta,
            3 => entry.HasCatalogs,
            4 => entry.HasError,
            _ => true,
        };
    }

    private void ObserveEntries()
    {
        foreach (var entry in Addons)
        {
            if (_observed.Add(entry))
            {
                entry.PropertyChanged += Entry_PropertyChanged;
            }
        }

        foreach (var entry in _observed.Where(entry => !Addons.Contains(entry)).ToList())
        {
            entry.PropertyChanged -= Entry_PropertyChanged;
            _observed.Remove(entry);
        }
    }

    private void Entry_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Capabilities and errors change which filter a row matches.
        if (e.PropertyName is nameof(AddonEntry.Manifest)
            or nameof(AddonEntry.LoadError)
            or nameof(AddonEntry.IsEnabled)
            or nameof(AddonEntry.Order))
        {
            RebuildVisible();
        }
    }

    private void SetError(string message)
    {
        HasError = true;
        ErrorMessage = message;
    }

    private void SetNotice(string message)
    {
        NoticeMessage = message;
        HasNotice = true;
    }

    private void RaiseAddonInfo()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasAddons));
        OnPropertyChanged(nameof(HasManyAddons));
        OnPropertyChanged(nameof(AddonCountText));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(VisibleCountText));
    }

    private void RaiseWatchHistoryInfo()
    {
        OnPropertyChanged(nameof(HasWatchHistory));
        OnPropertyChanged(nameof(WatchHistoryText));
    }

    private void RaiseSearchHistoryInfo()
    {
        OnPropertyChanged(nameof(HasSearchHistory));
        OnPropertyChanged(nameof(SearchHistoryText));
    }
}


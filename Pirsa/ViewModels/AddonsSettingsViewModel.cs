using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pirsa.Services;

namespace Pirsa.ViewModels;

/// <summary>
/// Settings → Add-ons. Adds, removes, enables and orders persisted Stremio
/// manifest URLs. The list order is the add-on priority order used by metadata
/// and stream discovery.
/// </summary>
public partial class AddonsSettingsViewModel : ObservableObject
{
    /// <summary>Above this many add-ons, searching and filtering appear.</summary>
    private const int SearchThreshold = 5;

    private readonly AddonRegistry _registry;
    private readonly HashSet<AddonEntry> _observed = [];
    private readonly EventHandler _registryChangedHandler;
    private readonly NotifyCollectionChangedEventHandler _entriesChangedHandler;
    private bool _loadingLinkRegistration;
    private bool _rebuilding;

    public AddonsSettingsViewModel(AddonRegistry registry)
    {
        _registry = registry;

        NewManifestUrl = "";
        SearchText = "";
        ErrorMessage = "";
        NoticeMessage = "";

        _registryChangedHandler = (_, _) =>
        {
            ObserveEntries();
            RaiseAddonInfo();
            RebuildVisible();
        };

        _entriesChangedHandler = (_, _) => ObserveEntries();

        Attach();
    }

    /// <summary>Subscribes to the shared add-on list (also after a cached navigation).</summary>
    public void Attach()
    {
        _registry.Changed -= _registryChangedHandler;
        _registry.Entries.CollectionChanged -= _entriesChangedHandler;
        _registry.Changed += _registryChangedHandler;
        _registry.Entries.CollectionChanged += _entriesChangedHandler;
    }

    /// <summary>Stops listening to the shared add-on list when the page goes away.</summary>
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

    public bool IsEmpty => Addons.Count == 0;

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
            var total = Addons.Count == 1 ? "1 add-on" : $"{Addons.Count} add-ons";
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

            _loadingLinkRegistration = true;
            HandlesStremioLinks = CanManageLinkRegistration && StremioProtocolRegistration.IsRegistered;
            _loadingLinkRegistration = false;

            await _registry.LoadManifestsAsync(cancellationToken, includeDisabled: true);
            RaiseAddonInfo();
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

    /// <summary>Moves an addon to a dropped position. Disabled while filtered.</summary>
    public async Task MoveToAsync(
        AddonEntry entry,
        int targetIndex,
        CancellationToken cancellationToken = default)
    {
        if (!CanReorder)
        {
            return;
        }

        try
        {
            await _registry.MoveToAsync(entry, targetIndex, cancellationToken);
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
    }

    public void ClearFilters()
    {
        SearchText = "";
        FilterIndex = 0;
    }

    public void DismissNotice() => HasNotice = false;

    /// <summary>Shows the same informational bar the add-on page uses.</summary>
    public void ShowNotice(string message) => SetNotice(message);

    /// <summary>Shows the same error bar the add-on page uses.</summary>
    public void ShowError(string message) => SetError(message);

    partial void OnSearchTextChanged(string value) => RebuildVisible();

    partial void OnFilterIndexChanged(int value) => RebuildVisible();

    /// <summary>Applies the search text and capability filter to the row list.</summary>
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
        ErrorMessage = message;
        HasError = true;
        HasNotice = false;
        NoticeMessage = "";
    }

    private void SetNotice(string message)
    {
        NoticeMessage = message;
        HasNotice = true;
        HasError = false;
        ErrorMessage = "";
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
}

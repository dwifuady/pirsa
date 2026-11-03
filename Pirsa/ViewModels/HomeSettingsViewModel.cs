using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pirsa.Services;
using Pirsa.Models;

namespace Pirsa.ViewModels;

/// <summary>
/// Settings → Home. One flat, ordered list of every pinned catalog across all
/// addons (the Home row order), plus every catalog an addon offers but that
/// isn't pinned yet, grouped by addon.
/// </summary>
public partial class HomeSettingsViewModel : ObservableObject
{
    private readonly AddonRegistry _registry;
    private bool _mutatingPins;
    private bool _suppressRegistryChange;

    private readonly EventHandler _registryChangedHandler;

    public HomeSettingsViewModel(AddonRegistry registry)
    {
        _registry = registry;

        _registryChangedHandler = (_, _) =>
        {
            if (!_suppressRegistryChange)
            {
                Rebuild();
            }
        };

        _registry.Changed += _registryChangedHandler;
    }

    /// <summary>The pinned-catalog references, in display order.</summary>
    private List<HomeRef> CurrentPinnedRefs() =>
        [.. Pinned.Select(item => new HomeRef
        {
            Url = item.Entry.ManifestUrl,
            Type = item.Type,
            Id = item.Id,
        })];

    /// <summary>Call again (after navigation) to re-subscribe to a cached page.</summary>
    public void Reattach()
    {
        _registry.Changed -= _registryChangedHandler;
        _registry.Changed += _registryChangedHandler;
    }

    public void Detach()
    {
        _registry.Changed -= _registryChangedHandler;
    }

    /// <summary>Pinned catalogs, one row per Home rail, in row order.</summary>
    public ObservableCollection<HomePinItemViewModel> Pinned { get; } = [];

    /// <summary>Not-pinned catalogs grouped by the addon that provides them.</summary>
    public ObservableCollection<HomeCatalogGroupViewModel> Available { get; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool HasPinned => Pinned.Count > 0;

    public bool HasNoPinned => !HasPinned;

    public bool HasAvailable => Available.Any(group => group.Items.Count > 0);

    public bool HasNoAvailable => !HasAvailable;

    public string SummaryText => _registry.HomeSummary;

    public string PinnedLabel => Pinned.Count == 0
        ? "On Home"
        : Pinned.Count == 1 ? "On Home · 1 row" : $"On Home · {Pinned.Count} rows";

    public string AvailableLabel
    {
        get
        {
            var count = Available.Sum(group => group.Items.Count);
            return count == 0
                ? "Available catalogs"
                : count == 1 ? "Available catalogs · 1" : $"Available catalogs · {count}";
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Reattach();
        Rebuild();

        IsBusy = true;
        try
        {
            await _registry.EnsureLoadedAsync(cancellationToken);
            await _registry.LoadManifestsAsync(cancellationToken, includeDisabled: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Adds one catalog to the end of the Home order.</summary>
    public async Task PinAsync(HomeCatalogOptionViewModel option)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _registry.SetHomeCatalogAsync(option.Entry, option.Type, option.Id, on: true);
            Rebuild();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Removes one catalog from the Home order.</summary>
    public async Task UnpinAsync(HomePinItemViewModel item)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _mutatingPins = true;
            try
            {
                Pinned.Remove(item);
            }
            finally
            {
                _mutatingPins = false;
            }

            await _registry.SetHomeCatalogAsync(item.Entry, item.Type, item.Id, on: false);
            Rebuild();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Moves one pinned row up (-1) or down (+1) in the global order.</summary>
    public async Task MovePinnedAsync(HomePinItemViewModel item, int offset)
    {
        if (IsBusy)
        {
            return;
        }

        var index = Pinned.IndexOf(item);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Pinned.Count)
        {
            return;
        }

        IsBusy = true;
        _mutatingPins = true;
        try
        {
            Pinned.Move(index, target);
            RefreshMoveFlags();
            _suppressRegistryChange = true;
            await _registry.SetHomeOrderAsync(CurrentPinnedRefs());
        }
        finally
        {
            _suppressRegistryChange = false;
            _mutatingPins = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Rebuilds both lists from the registry. Skipped when the resulting pin
    /// list is identical, so drag-reorder and row buttons keep their
    /// containers and keyboard focus.
    /// </summary>
    private void Rebuild()
    {
        var pinned = new List<HomePinItemViewModel>();
        if (_registry.HomeConfigured)
        {
            foreach (var reference in _registry.HomeList)
            {
                var entry = _registry.GetEntry(reference.Url);
                var manifest = entry?.Manifest;
                if (entry is null || manifest?.SupportsCatalog != true)
                {
                    continue;
                }

                var catalog = manifest.Catalogs.FirstOrDefault(candidate =>
                    candidate.Type.Equals(reference.Type, StringComparison.OrdinalIgnoreCase) &&
                    candidate.Id.Equals(reference.Id, StringComparison.OrdinalIgnoreCase));
                if (catalog is null)
                {
                    continue;
                }

                pinned.Add(new HomePinItemViewModel(entry, reference.Type, reference.Id, catalog));
            }
        }
        else
        {
            foreach (var entry in _registry.Entries)
            {
                var manifest = entry.Manifest;
                if (!entry.IsEnabled || !entry.IsDefault || manifest?.SupportsCatalog != true)
                {
                    continue;
                }

                foreach (var catalog in manifest.Catalogs)
                {
                    pinned.Add(new HomePinItemViewModel(
                        entry, catalog.Type, catalog.Id, catalog));
                }
            }
        }

        var available = new List<HomeCatalogGroupViewModel>();
        foreach (var entry in _registry.Entries)
        {
            var manifest = entry.Manifest;
            if (!entry.IsEnabled || manifest?.SupportsCatalog != true)
            {
                continue;
            }

            var group = new HomeCatalogGroupViewModel(entry);
            foreach (var catalog in manifest.Catalogs)
            {
                if (!pinned.Any(item =>
                        ReferenceEquals(item.Entry, entry) &&
                        item.Type.Equals(catalog.Type, StringComparison.OrdinalIgnoreCase) &&
                        item.Id.Equals(catalog.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    group.Items.Add(new HomeCatalogOptionViewModel(
                        entry, catalog.Type, catalog.Id, catalog));
                }
            }

            if (group.Items.Count > 0)
            {
                available.Add(group);
            }
        }

        var identical = Pinned.Count == pinned.Count &&
            Pinned.Zip(pinned, (existing, built) =>
                ReferenceEquals(existing.Entry, built.Entry) &&
                existing.Type.Equals(built.Type, StringComparison.OrdinalIgnoreCase) &&
                existing.Id.Equals(built.Id, StringComparison.OrdinalIgnoreCase))
                .All(match => match);

        if (identical)
        {
            // Titles can still change when a manifest reloads; refresh them.
            for (var i = 0; i < pinned.Count; i++)
            {
                Pinned[i].RefreshFrom(pinned[i]);
            }
        }
        else
        {
            _mutatingPins = true;
            try
            {
                Pinned.Clear();
                foreach (var item in pinned)
                {
                    Pinned.Add(item);
                }
            }
            finally
            {
                _mutatingPins = false;
            }

            RefreshMoveFlags();
        }

        if (!Available.SequenceEqual(available))
        {
            Available.Clear();
            foreach (var group in available)
            {
                Available.Add(group);
            }
        }

        Raise();
    }

    private void RefreshMoveFlags()
    {
        for (var index = 0; index < Pinned.Count; index++)
        {
            Pinned[index].CanMoveUp = index > 0;
            Pinned[index].CanMoveDown = index < Pinned.Count - 1;
        }
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(HasPinned));
        OnPropertyChanged(nameof(HasNoPinned));
        OnPropertyChanged(nameof(HasAvailable));
        OnPropertyChanged(nameof(HasNoAvailable));
        OnPropertyChanged(nameof(PinnedLabel));
        OnPropertyChanged(nameof(AvailableLabel));
        OnPropertyChanged(nameof(SummaryText));
    }
}

/// <summary>One pinned Home row: movable anywhere in the global order.</summary>
public partial class HomePinItemViewModel : ObservableObject
{
    public HomePinItemViewModel(
        AddonEntry entry,
        string type,
        string id,
        AddonCatalog catalog)
    {
        Entry = entry;
        Type = type;
        Id = id;
        Title = catalog.DisplayName;
        ValueLine = ResolveValueLine(catalog);
    }

    public AddonEntry Entry { get; }

    public string Type { get; }

    public string Id { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string? ValueLine { get; set; }

    [ObservableProperty]
    public partial bool CanMoveUp { get; set; }

    [ObservableProperty]
    public partial bool CanMoveDown { get; set; }

    public bool HasValueLine => !string.IsNullOrWhiteSpace(ValueLine);

    public string AddonName => Entry.Name;

    private static string? ResolveValueLine(AddonCatalog catalog) =>
        catalog.RequiredGenreValue is { } value ? $"{catalog.FilterLabel}: {value}" : null;

    /// <summary>Refreshes display strings in place so a rebuild can be skipped.</summary>
    public void RefreshFrom(HomePinItemViewModel source)
    {
        Title = source.Title;
        ValueLine = source.ValueLine;
    }
}

/// <summary>One catalog an addon offers that isn't pinned yet.</summary>
public partial class HomeCatalogOptionViewModel(AddonEntry entry, string type, string id, AddonCatalog catalog) :
    ObservableObject
{
    public AddonEntry Entry { get; } = entry;

    public string Type { get; } = type;

    public string Id { get; } = id;

    public string Title { get; } = catalog.DisplayName;

    public string? ValueLine { get; } =
        catalog.RequiredGenreValue is { } value ? $"{catalog.FilterLabel}: {value}" : null;

    public bool HasValueLine => !string.IsNullOrWhiteSpace(ValueLine);
}

/// <summary>The unpinned catalogs of one addon.</summary>
public sealed class HomeCatalogGroupViewModel(AddonEntry entry)
{
    public AddonEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    public string Host => Entry.Host;

    public ObservableCollection<HomeCatalogOptionViewModel> Items { get; } = [];
}

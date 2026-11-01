using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Data;
using Pirsa.Models;
using Pirsa.Services;

namespace Pirsa.ViewModels;

/// <summary>
/// Discover: browse the catalogs addons provide, or search every addon that
/// declares a <c>search</c> extra and group the hits by addon. Browsing uses the
/// type / catalog / genre dropdowns; searching keeps the type filter and spans
/// all addons.
/// </summary>
public partial class DiscoverViewModel : ObservableObject
{
    private const string FilterAllGenres = "All genres";
    private const string FilterAllYears = "All years";
    private const int SearchDebounceMilliseconds = 300;

    private readonly AddonRegistry _registry;
    private List<DiscoverCatalogOption> _allCatalogs = [];
    private CancellationTokenSource? _reloadCts;
    private CancellationTokenSource? _searchCts;
    private string? _allFilter;
    private bool _suspend;
    private bool _ready;
    private int _searchFailures;
    private string? _lastSearchError;

    public DiscoverViewModel(AddonRegistry registry)
    {
        _registry = registry;
        SearchText = "";
        SearchPlaceholderText = "Search across your addons";
        ResultsHeading = "Results";
        ResultsCountText = "";
        ErrorMessage = "";
        LoadErrorMessage = "";
        SearchSource = new CollectionViewSource
        {
            IsSourceGrouped = true,
            Source = SearchGroups,
        };
        SearchView = SearchSource.View;
    }

    public ObservableCollection<DiscoverTypeOption> Types { get; } = [];

    public ObservableCollection<DiscoverCatalogOption> Catalogs { get; } = [];

    public ObservableCollection<string> Filters { get; } = [];

    public ObservableCollection<MetaItem> Results { get; } = [];

    public ObservableCollection<DiscoverResultGroup> SearchGroups { get; } = [];

    /// <summary>Groups <see cref="SearchGroups"/> for the grouped results grid.</summary>
    public CollectionViewSource SearchSource { get; }

    /// <summary>The grouped view bound to the search results grid.</summary>
    public ICollectionView SearchView { get; }

    [ObservableProperty]
    public partial DiscoverTypeOption? SelectedType { get; set; }

    [ObservableProperty]
    public partial DiscoverCatalogOption? SelectedCatalog { get; set; }

    [ObservableProperty]
    public partial string? SelectedFilter { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    public partial string SearchPlaceholderText { get; set; }

    [ObservableProperty]
    public partial string FilterLabel { get; set; } = "Genre";

    [ObservableProperty]
    public partial bool HasFilters { get; set; }

    [ObservableProperty]
    public partial bool CanSearch { get; set; }

    [ObservableProperty]
    public partial bool CanLoadMore { get; set; }

    [ObservableProperty]
    public partial bool IsInitializing { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool HasLoadError { get; set; }

    [ObservableProperty]
    public partial string LoadErrorMessage { get; set; }

    [ObservableProperty]
    public partial string ResultsHeading { get; set; }

    [ObservableProperty]
    public partial string ResultsCountText { get; set; }

    public bool HasResults => Results.Count > 0;

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    public bool IsBrowsing => !IsSearching;

    public bool ShowSearchResults => IsSearching && SearchGroups.Count > 0;

    /// <summary>Type / catalog / genre only apply while browsing.</summary>
    public bool ShowBrowseFilters => !IsSearching;

    public bool ShowGenreFilter => HasFilters && !IsSearching;

    public int SearchResultCount => SearchGroups.Sum(group => group.Count);

    public bool ShowResultsHeader =>
        IsSearching
            ? ShowSearchResults || IsLoading
            : HasResults || IsLoading || IsLoadingMore;

    public bool ShowLoadingState =>
        !IsInitializing && !HasError && IsLoading &&
        (IsSearching ? SearchGroups.Count == 0 : !HasResults);

    public bool ShowEmptyState =>
        !IsInitializing && !HasError && !IsLoading && !HasLoadError &&
        (IsSearching
            ? SearchGroups.Count == 0
            : SelectedCatalog is not null && !HasResults);

    public string EmptyTitle =>
        IsSearching
            ? $"No results for \u201c{SearchText.Trim()}\u201d"
            : "Nothing here yet";

    public string EmptyMessage =>
        IsSearching
            ? "No addon returned a match. Try a different spelling or another type."
            : "Try another catalog, type or filter.";

    /// <summary>
    /// Loads manifests, builds the type/catalog/filter lists and fetches the
    /// first catalog page. Safe to call again after the addon list changes.
    /// </summary>
    [RelayCommand]
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsInitializing)
        {
            return;
        }

        IsInitializing = true;
        HasError = false;
        HasLoadError = false;
        ErrorMessage = "";
        _ready = false;

        try
        {
            await _registry.EnsureLoadedAsync(cancellationToken);

            if (_registry.Entries.Count == 0)
            {
                Fail("No addons are configured. Open Settings to add a Stremio addon.");
                return;
            }

            await _registry.LoadManifestsAsync(cancellationToken);

            _allCatalogs = BuildCatalogOptions(_registry.Entries);

            if (_allCatalogs.Count == 0)
            {
                Fail("Your addons don't expose any catalog this app can browse.");
                return;
            }

            var defaultOption =
                _allCatalogs.FirstOrDefault(option =>
                    option.Catalog.Type.Equals("movie", StringComparison.OrdinalIgnoreCase) &&
                    option.Catalog.Id.Equals("top", StringComparison.OrdinalIgnoreCase))
                ?? _allCatalogs.FirstOrDefault(option =>
                    option.Catalog.Id.Equals("top", StringComparison.OrdinalIgnoreCase))
                ?? _allCatalogs[0];

            _suspend = true;
            try
            {
                Types.Clear();
                Types.Add(DiscoverTypeOption.All);
                foreach (var typeOption in BuildTypeOptions(_allCatalogs))
                {
                    Types.Add(typeOption);
                }

                SelectedType =
                    Types.FirstOrDefault(option =>
                        !option.IsAll && option.Types.Contains(defaultOption.Catalog.Type, StringComparer.OrdinalIgnoreCase))
                    ?? Types[0];

                FillCatalogList(SelectedType, defaultOption);
            }
            finally
            {
                _suspend = false;
            }

            _ready = true;
            await StartLoadAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Fail(Describe(exception));
        }
        finally
        {
            IsInitializing = false;
            NotifyResultsState();
        }
    }

    /// <summary>Applies typed search text after a short debounce.</summary>
    public void SubmitSearch(string? text, bool immediate = false)
    {
        SearchText = text ?? "";

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        if (!_ready)
        {
            return;
        }

        if (immediate)
        {
            StartLoad();
            return;
        }

        // Keep the page in a loading state while the debounce runs so an empty
        // grid never flashes between keystrokes.
        IsLoading = true;
        NotifyResultsState();

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _ = DebouncedSearchAsync(cts);
    }

    [RelayCommand]
    private Task LoadMoreAsync() => ReloadAsync(append: true);

    [RelayCommand]
    private Task RetryAsync() => StartLoadAsync();

    partial void OnSelectedTypeChanged(DiscoverTypeOption? value)
    {
        if (_suspend || !_ready)
        {
            return;
        }

        _suspend = true;
        try
        {
            FillCatalogList(value, null);
        }
        finally
        {
            _suspend = false;
        }

        StartLoad();
    }

    partial void OnSelectedCatalogChanged(DiscoverCatalogOption? value)
    {
        if (_suspend || !_ready || value is null || IsSearching)
        {
            return;
        }

        _suspend = true;
        try
        {
            BuildFilters();
        }
        finally
        {
            _suspend = false;
        }

        StartLoad();
    }

    partial void OnSelectedFilterChanged(string? value)
    {
        if (_suspend || !_ready || IsSearching)
        {
            return;
        }

        StartLoad();
    }

    partial void OnSearchTextChanged(string value)
    {
        NotifyResultsState();
    }

    private async Task DebouncedSearchAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(SearchDebounceMilliseconds, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!cts.IsCancellationRequested && ReferenceEquals(_searchCts, cts))
        {
            StartLoad();
        }
    }

    /// <summary>Search spans every addon; browsing uses only the selected catalog.</summary>
    private void StartLoad()
    {
        if (IsSearching)
        {
            _ = SearchAllAsync();
        }
        else
        {
            _ = ReloadAsync();
        }
    }

    private Task StartLoadAsync() =>
        IsSearching ? SearchAllAsync() : ReloadAsync();

    private async Task ReloadAsync(bool append = false)
    {
        var option = SelectedCatalog;
        if (option is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _reloadCts, cts);
        previous?.Cancel();
        previous?.Dispose();

        if (!append)
        {
            SearchGroups.Clear();
        }

        await LoadCoreAsync(option, append, cts);
    }

    /// <summary>
    /// Searches every catalog that declares a <c>search</c> extra, across all
    /// addons and media types, and groups the hits by addon + type.
    /// </summary>
    private async Task SearchAllAsync()
    {
        var search = SearchText.Trim();
        if (search.Length == 0)
        {
            await ReloadAsync();
            return;
        }

        var catalogs = _allCatalogs
            .Where(SupportsGlobalSearch)
            .ToList();

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _reloadCts, cts);
        previous?.Cancel();
        previous?.Dispose();
        var isCurrent = () => ReferenceEquals(_reloadCts, cts);

        IsLoading = true;
        HasLoadError = false;
        LoadErrorMessage = "";
        CanLoadMore = false;
        _searchFailures = 0;
        _lastSearchError = null;
        SearchGroups.Clear();
        Results.Clear();
        ResultsHeading = $"Search results for \u201c{search}\u201d";
        ResultsCountText = "";
        NotifyResultsState();

        if (catalogs.Count == 0)
        {
            IsLoading = false;
            NotifyResultsState();
            return;
        }

        var pending = catalogs
            .GroupBy(option => option.Entry)
            .Select(group => SearchAddonAsync(group.Key, group.ToList(), search, cts.Token))
            .ToList();

        try
        {
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending);
                pending.Remove(finished);

                var results = await finished;
                if (!isCurrent())
                {
                    return;
                }

                if (results.Count == 0)
                {
                    continue;
                }

                foreach (var result in results)
                {
                    InsertSearchGroup(result);
                }

                ResultsCountText = SearchResultCount == 1
                    ? "1 title"
                    : $"{SearchResultCount} titles";
                NotifyResultsState();
            }

            if (_searchFailures > 0 && isCurrent())
            {
                HasLoadError = true;
                LoadErrorMessage = _searchFailures == 1
                    ? _lastSearchError ?? "An addon couldn't be searched."
                    : $"{_searchFailures} addons couldn't be searched. Last error \u2014 {_lastSearchError}";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (isCurrent())
            {
                HasLoadError = true;
                LoadErrorMessage = Describe(exception);
            }
        }
        finally
        {
            if (isCurrent())
            {
                IsLoading = false;
                NotifyResultsState();
            }
        }
    }

    private async Task<IReadOnlyList<DiscoverResultGroup>> SearchAddonAsync(
        AddonEntry entry,
        IReadOnlyList<DiscoverCatalogOption> catalogs,
        string search,
        CancellationToken cancellationToken)
    {
        var addonOrder = _registry.Entries.IndexOf(entry);
        var groups = new List<DiscoverResultGroup>();
        var failed = false;

        foreach (var byType in catalogs.GroupBy(
            option => option.Catalog.Type.ToLowerInvariant(),
            StringComparer.OrdinalIgnoreCase))
        {
            var items = new List<MetaItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var option in byType)
            {
                try
                {
                    var metas = await option.Entry.Client.GetCatalogAsync(
                        option.Catalog,
                        new CatalogQuery { Search = search },
                        cancellationToken);

                    foreach (var meta in metas)
                    {
                        meta.SourceAddonUrl = entry.ManifestUrl;
                        if (seen.Add(ItemKey(meta)))
                        {
                            items.Add(meta);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failed = true;
                    _lastSearchError = $"{entry.Name}: {Describe(exception)}";
                }
            }

            if (items.Count == 0)
            {
                continue;
            }

            var typeLabel = MediaTypeLabels.Label(byType.Key);
            groups.Add(new DiscoverResultGroup(
                entry.Name,
                addonOrder * 100 + MediaTypeLabels.Order(typeLabel),
                typeLabel,
                items));
        }

        if (failed)
        {
            Interlocked.Increment(ref _searchFailures);
        }

        return groups;
    }

    private void InsertSearchGroup(DiscoverResultGroup group)
    {
        var index = SearchGroups.Count;
        while (index > 0 && SearchGroups[index - 1].Order > group.Order)
        {
            index--;
        }

        SearchGroups.Insert(index, group);
    }

    private async Task LoadCoreAsync(
        DiscoverCatalogOption option,
        bool append,
        CancellationTokenSource cts)
    {
        var cancellationToken = cts.Token;
        var isCurrent = () => ReferenceEquals(_reloadCts, cts);

        if (append)
        {
            IsLoadingMore = true;
        }
        else
        {
            IsLoading = true;
            HasLoadError = false;
            LoadErrorMessage = "";
            CanLoadMore = false;
        }

        NotifyResultsState();

        try
        {
            var search = SearchText.Trim();
            var query = new CatalogQuery
            {
                Genre = ResolveFilterValue(),
                Search = string.IsNullOrWhiteSpace(search) ? null : search,
                Skip = append ? Results.Count : 0,
            };

            var metas = await option.Entry.Client.GetCatalogAsync(
                option.Catalog,
                query,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (!isCurrent())
            {
                return;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (append)
            {
                foreach (var existing in Results)
                {
                    seen.Add(ItemKey(existing));
                }
            }
            else
            {
                Results.Clear();
            }

            var added = 0;
            foreach (var meta in metas)
            {
                meta.SourceAddonUrl = option.Entry.ManifestUrl;
                if (seen.Add(ItemKey(meta)))
                {
                    Results.Add(meta);
                    added++;
                }
            }

            CanLoadMore =
                option.Catalog.SupportsSkip &&
                metas.Count > 0 &&
                (!append || added > 0);

            ResultsHeading = string.IsNullOrWhiteSpace(query.Search)
                ? "Results"
                : $"Results for \u201c{query.Search}\u201d";
            ResultsCountText = Results.Count == 1 ? "1 title" : $"{Results.Count} titles";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!isCurrent())
            {
                return;
            }

            HasLoadError = true;
            LoadErrorMessage = Describe(exception);
            CanLoadMore = false;
        }
        finally
        {
            if (isCurrent())
            {
                IsLoading = false;
                IsLoadingMore = false;
                NotifyResultsState();
            }
        }
    }

    private void FillCatalogList(DiscoverTypeOption? type, DiscoverCatalogOption? preferred)
    {
        var keep = preferred;
        if (keep is null && SelectedCatalog is { } current &&
            (type is null || type.IsAll || type.Types.Contains(current.Catalog.Type, StringComparer.OrdinalIgnoreCase)))
        {
            keep = current;
        }

        Catalogs.Clear();
        var showType = type is null || type.IsAll;
        foreach (var option in _allCatalogs)
        {
            option.ShowType = showType;

            if (type is null || type.IsAll ||
                type.Types.Contains(option.Catalog.Type, StringComparer.OrdinalIgnoreCase))
            {
                Catalogs.Add(option);
            }
        }

        SelectedCatalog = keep is not null && Catalogs.Contains(keep)
            ? keep
            : Catalogs.FirstOrDefault();

        BuildFilters();
    }

    private void BuildFilters()
    {
        var option = SelectedCatalog;
        var extra = option?.Catalog.FilterExtra;

        FilterLabel = option?.Catalog.FilterLabel ?? "Genre";
        CanSearch = _allCatalogs.Any(SupportsGlobalSearch);
        SearchPlaceholderText = CanSearch
            ? "Search across your addons"
            : "No addon provides search";

        Filters.Clear();
        _allFilter = null;

        if (extra is { Options.Count: > 0 })
        {
            _allFilter = extra.IsYear || extra.Options.All(CatalogExtra.IsYearValue)
                ? FilterAllYears
                : FilterAllGenres;

            if (!extra.IsRequired)
            {
                Filters.Add(_allFilter);
            }

            foreach (var value in extra.Options)
            {
                if (!Filters.Contains(value))
                {
                    Filters.Add(value);
                }
            }

            SelectedFilter = extra.IsRequired
                ? DefaultFilterValue(extra)
                : _allFilter;
        }
        else
        {
            SelectedFilter = null;
            FilterLabel = "Genre";
        }

        HasFilters = Filters.Count > 0;
    }

    private static string DefaultFilterValue(CatalogExtra extra)
    {
        var currentYear = DateTime.Now.Year.ToString(CultureInfo.InvariantCulture);
        return extra.Options.FirstOrDefault(option => option == currentYear) ?? extra.Options[0];
    }

    private bool InTypeScope(DiscoverCatalogOption option) =>
        SelectedType is null || SelectedType.IsAll ||
        SelectedType.Types.Contains(option.Catalog.Type, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A global search sends only the search term, so skip catalogs whose
    /// genre/year extra is mandatory (they'd reject a filterless query).
    /// </summary>
    private static bool SupportsGlobalSearch(DiscoverCatalogOption option) =>
        option.Catalog.SupportsSearch &&
        !option.Catalog.Extra.Any(extra => extra.IsRequired && extra.IsFilterExtra);

    private string? ResolveFilterValue()
    {
        if (string.IsNullOrWhiteSpace(SelectedFilter))
        {
            return null;
        }

        return _allFilter is not null && SelectedFilter == _allFilter ? null : SelectedFilter;
    }

    private void Fail(string message)
    {
        HasError = true;
        ErrorMessage = message;
        Results.Clear();
        NotifyResultsState();
    }

    private void NotifyResultsState()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(IsBrowsing));
        OnPropertyChanged(nameof(ShowSearchResults));
        OnPropertyChanged(nameof(ShowBrowseFilters));
        OnPropertyChanged(nameof(ShowGenreFilter));
        OnPropertyChanged(nameof(SearchResultCount));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowResultsHeader));
        OnPropertyChanged(nameof(ShowLoadingState));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private static List<DiscoverCatalogOption> BuildCatalogOptions(IEnumerable<AddonEntry> entries)
    {
        var options = new List<DiscoverCatalogOption>();

        foreach (var entry in entries)
        {
            var manifest = entry.Manifest;
            if (manifest is null || !manifest.SupportsCatalog)
            {
                continue;
            }

            foreach (var catalog in manifest.Catalogs)
            {
                if (string.IsNullOrWhiteSpace(catalog.Type) ||
                    string.IsNullOrWhiteSpace(catalog.Id) ||
                    !catalog.IsBrowsable)
                {
                    continue;
                }

                options.Add(new DiscoverCatalogOption(entry, catalog));
            }
        }

        return options;
    }

    private static List<DiscoverTypeOption> BuildTypeOptions(IReadOnlyList<DiscoverCatalogOption> catalogs)
    {
        var groups = new List<(string Label, List<string> Types)>();

        foreach (var option in catalogs)
        {
            var label = MediaTypeLabels.Label(option.Catalog.Type);
            var index = groups.FindIndex(group =>
                group.Label.Equals(label, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                groups.Add((label, [option.Catalog.Type.ToLowerInvariant()]));
                continue;
            }

            var types = groups[index].Types;
            if (!types.Contains(option.Catalog.Type, StringComparer.OrdinalIgnoreCase))
            {
                types.Add(option.Catalog.Type.ToLowerInvariant());
            }
        }

        return groups
            .OrderBy(group => TypeOrder(group.Label))
            .ThenBy(group => group.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new DiscoverTypeOption(group.Label, group.Types))
            .ToList();
    }

    private static int TypeOrder(string label) => MediaTypeLabels.Order(label);

    private static string ItemKey(MetaItem item) =>
        $"{item.Type}\u001f{item.Id}";

    private static string Describe(Exception exception) => exception switch
    {
        StremioException => exception.Message,
        HttpRequestException => "Couldn't reach the addon. Check your connection and try again.",
        TaskCanceledException => "The addon took too long to respond.",
        _ => "Something went wrong while loading this catalog.",
    };
}

/// <summary>One entry in the Type dropdown. "All" matches every catalog.</summary>
public sealed class DiscoverTypeOption(string label, List<string> types)
{
    public static DiscoverTypeOption All { get; } = new("All", []);

    public string Label { get; } = label;

    public IReadOnlyList<string> Types { get; } = types;

    public bool IsAll => Types.Count == 0;

    public override string ToString() => Label;
}

/// <summary>One entry in the Catalog dropdown: an addon catalog and its owner.</summary>
public sealed partial class DiscoverCatalogOption(AddonEntry entry, AddonCatalog catalog) : ObservableObject
{
    public AddonEntry Entry { get; } = entry;

    public AddonCatalog Catalog { get; } = catalog;

    public string Name { get; } = NameOf(catalog);

    /// <summary>
    /// True while the Type dropdown is "All"; the label then carries the
    /// media type so duplicate catalog names stay distinguishable.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowType { get; set; }

    public string Label => ShowType
        ? $"{Name} \u00b7 {MediaTypeLabels.Label(Catalog.Type)} \u00b7 {Entry.Name}"
        : $"{Name} \u00b7 {Entry.Name}";

    public override string ToString() => Label;

    partial void OnShowTypeChanged(bool value) => OnPropertyChanged(nameof(Label));

    private static string NameOf(AddonCatalog catalog) =>
        string.IsNullOrWhiteSpace(catalog.Name)
            ? (string.IsNullOrWhiteSpace(catalog.Type) ? "Catalog" : catalog.Type)
            : catalog.Name.Trim();
}

/// <summary>Display names for Stremio media types, including addon-defined ones.</summary>
internal static class MediaTypeLabels
{
    public static string Label(string type) => type.ToLowerInvariant() switch
    {
        "movie" => "Movies",
        "series" => "Series",
        "tv" or "channel" => "Live TV",
        "music" => "Music",
        "podcast" => "Podcasts",
        _ => string.IsNullOrWhiteSpace(type)
            ? "Other"
            : char.ToUpper(type[0], CultureInfo.InvariantCulture) + type[1..],
    };

    /// <summary>Fixed section order for search groups: Movies, Series, then the rest.</summary>
    public static int Order(string label) => label switch
    {
        "Movies" => 0,
        "Series" => 1,
        "Live TV" => 2,
        "Music" => 3,
        "Podcasts" => 4,
        _ => 5,
    };
}

/// <summary>
/// Search hits from one addon. The group itself is the collection the grouped
/// results grid renders, with the addon header above its posters.
/// </summary>
public sealed class DiscoverResultGroup : ObservableCollection<MetaItem>
{
    public DiscoverResultGroup(
        string addonName,
        int order,
        string typeName,
        IEnumerable<MetaItem> items)
        : base(items)
    {
        AddonName = addonName;
        Order = order;
        TypeName = typeName;
    }

    public string AddonName { get; }

    public int Order { get; }

    public string TypeName { get; }

    public string CountLabel => Count == 1 ? "1 title" : $"{Count} titles";

    public override string ToString() => $"{AddonName} {TypeName}";
}

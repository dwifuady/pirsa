using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Pirsa.Models;
using Pirsa.Services;

namespace Pirsa.ViewModels;

/// <summary>
/// Home feed: a Top 10 hero taken from one catalog, followed by every catalog
/// exposed by the user's addons. The hero catalog is still a constant (movie/top
/// when available) and is the one piece meant to become configurable.
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private const string HeroType = "movie";
    private const string HeroCatalogId = "top";
    private const int HeroLimit = 10;

    private readonly AddonRegistry _registry;
    private readonly WatchProgressStore _watchProgress;
    private int _rowFailures;

    public HomeViewModel(AddonRegistry registry, WatchProgressStore watchProgress)
    {
        _registry = registry;
        _watchProgress = watchProgress;
        ErrorMessage = "";
        RowErrorMessage = "";
        AddonName = "";
    }

    public ObservableCollection<HeroItemViewModel> HeroItems { get; } = [];

    public ObservableCollection<CatalogRowViewModel> Catalogs { get; } = [];

    /// <summary>One card per movie or series from the local watch history.</summary>
    public ObservableCollection<WatchProgressEntry> ContinueWatching { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingRows { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool HasRowErrors { get; set; }

    [ObservableProperty]
    public partial string RowErrorMessage { get; set; }

    [ObservableProperty]
    public partial string AddonName { get; set; }

    [ObservableProperty]
    public partial int HeroSlides { get; set; }

    public bool IsEmpty =>
        !IsLoading && !IsLoadingRows && !HasError &&
        HeroItems.Count == 0 && Catalogs.Count == 0 && ContinueWatching.Count == 0;

    public bool HasContinueWatching => ContinueWatching.Count > 0;

    public string ContinueWatchingLabel => ContinueWatching.Count == 1
        ? "1 title"
        : $"{ContinueWatching.Count} titles";

    /// <summary>
    /// Reloads the local watch history. Cheap enough to run on every visit to
    /// Home, which keeps the rail current after watching something.
    /// </summary>
    public async Task RefreshContinueWatchingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _watchProgress.EnsureLoadedAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            return;
        }

        ContinueWatching.Clear();
        foreach (var entry in _watchProgress.GetContinueWatching())
        {
            ContinueWatching.Add(entry);
        }

        OnPropertyChanged(nameof(HasContinueWatching));
        OnPropertyChanged(nameof(ContinueWatchingLabel));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public async Task RemoveContinueWatchingAsync(
        WatchProgressEntry entry,
        CancellationToken cancellationToken = default)
    {
        await _watchProgress.RemoveTitleAsync(entry.MetaId, cancellationToken);
        await RefreshContinueWatchingAsync(cancellationToken);
    }

    public string LoadingText =>
        string.IsNullOrWhiteSpace(AddonName) ? "Loading your catalog…" : $"Loading {AddonName}…";

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        IsLoadingRows = false;
        HasError = false;
        HasRowErrors = false;
        ErrorMessage = "";
        RowErrorMessage = "";
        _rowFailures = 0;
        HeroItems.Clear();
        Catalogs.Clear();
        HeroSlides = 0;
        OnPropertyChanged(nameof(IsEmpty));

        try
        {
            await _registry.EnsureLoadedAsync(cancellationToken);

            if (_registry.EnabledEntries.Count == 0)
            {
                HasError = true;
                ErrorMessage = "No addons are enabled. Open Settings to turn one on or add a Stremio addon.";
                return;
            }

            await _registry.LoadManifestsAsync(cancellationToken);

            var entries = _registry.EnabledEntries
                .Where(entry => entry.Manifest is not null)
                .ToList();

            if (entries.Count == 0)
            {
                HasError = true;
                ErrorMessage = "Couldn't load any addon manifests. Check your connection, or manage addons in Settings.";
                return;
            }

            AddonName = entries.Count == 1 ? entries[0].Name : $"{entries.Count} addons";
            OnPropertyChanged(nameof(LoadingText));

            var catalogRefs = new List<CatalogRef>();
            var order = 0;
            foreach (var entry in entries)
            {
                var manifest = entry.Manifest!;
                if (!manifest.SupportsCatalog)
                {
                    continue;
                }

                foreach (var catalog in manifest.Catalogs.Where(IsUsableCatalog))
                {
                    catalogRefs.Add(new CatalogRef(entry, catalog, order++));
                }
            }

            if (catalogRefs.Count == 0)
            {
                HasError = true;
                ErrorMessage = "Your addons don't expose any catalog this app can browse.";
                return;
            }

            var heroRef = PickHeroCatalog(catalogRefs);
            var heroTitle = BuildCatalogTitle(heroRef.Catalog);
            var heroGenre = ResolveGenre(heroRef.Catalog, out _);

            var heroMetas = await heroRef.Entry.Client.GetCatalogAsync(
                heroRef.Catalog,
                new CatalogQuery { Genre = heroGenre },
                cancellationToken);

            if (heroMetas.Count == 0)
            {
                throw new StremioException(
                    $"{heroRef.Entry.Name} didn't return any titles for {heroTitle}.");
            }

            StampSource(heroMetas, heroRef.Entry.ManifestUrl);

            var rank = 1;
            foreach (var meta in heroMetas.Take(HeroLimit))
            {
                HeroItems.Add(new HeroItemViewModel(
                    meta,
                    rank++,
                    $"{heroRef.Entry.Name} · {heroTitle}"));
            }

            HeroSlides = HeroItems.Count;
            OnPropertyChanged(nameof(IsEmpty));

            // The hero is on screen now; let the rest of the page fill in.
            IsLoading = false;

            var remaining = catalogRefs
                .Where(catalogRef => !ReferenceEquals(catalogRef, heroRef))
                .ToList();

            IsLoadingRows = remaining.Count > 0;
            var pending = remaining
                .Select(catalogRef => LoadRowAsync(catalogRef, catalogRef.Order, cancellationToken))
                .ToList();

            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending);
                pending.Remove(finished);

                if (await finished is { } row)
                {
                    InsertRow(row);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            HasError = true;
            ErrorMessage = Describe(exception);
        }
        finally
        {
            IsLoading = false;
            IsLoadingRows = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private Task RetryRowsAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    private async Task<CatalogRowViewModel?> LoadRowAsync(
        CatalogRef catalogRef,
        int order,
        CancellationToken cancellationToken)
    {
        try
        {
            var genre = ResolveGenre(catalogRef.Catalog, out var subtitle);
            var metas = await catalogRef.Entry.Client.GetCatalogAsync(
                catalogRef.Catalog,
                new CatalogQuery { Genre = genre },
                cancellationToken);

            if (metas.Count == 0)
            {
                return null;
            }

            StampSource(metas, catalogRef.Entry.ManifestUrl);

            return new CatalogRowViewModel(
                order,
                BuildCatalogTitle(catalogRef.Catalog),
                subtitle,
                catalogRef.Entry.Name,
                metas.ToList());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _rowFailures++;
            HasRowErrors = true;
            RowErrorMessage = _rowFailures == 1
                ? $"{catalogRef.Entry.Name}: {Describe(exception)}"
                : $"{_rowFailures} catalogs couldn't load. Last error — {catalogRef.Entry.Name}: {Describe(exception)}";
            return null;
        }
    }

    private void InsertRow(CatalogRowViewModel row)
    {
        var index = 0;
        while (index < Catalogs.Count && Catalogs[index].Order <= row.Order)
        {
            index++;
        }

        Catalogs.Insert(index, row);
    }

    private static void StampSource(IEnumerable<MetaItem> metas, string manifestUrl)
    {
        foreach (var meta in metas)
        {
            meta.SourceAddonUrl = manifestUrl;
        }
    }

    private static CatalogRef PickHeroCatalog(IReadOnlyList<CatalogRef> catalogs) =>
        catalogs.FirstOrDefault(catalogRef =>
            catalogRef.Catalog.Type.Equals(HeroType, StringComparison.OrdinalIgnoreCase) &&
            catalogRef.Catalog.Id.Equals(HeroCatalogId, StringComparison.OrdinalIgnoreCase))
        ?? catalogs.FirstOrDefault(catalogRef =>
            catalogRef.Catalog.Id.Equals(HeroCatalogId, StringComparison.OrdinalIgnoreCase))
        ?? catalogs.FirstOrDefault(catalogRef =>
            catalogRef.Catalog.Type.Equals(HeroType, StringComparison.OrdinalIgnoreCase))
        ?? catalogs[0];

    private static bool IsUsableCatalog(AddonCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog.Type) || string.IsNullOrWhiteSpace(catalog.Id))
        {
            return false;
        }

        foreach (var required in catalog.ExtraRequired)
        {
            if (!required.Equals("genre", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        foreach (var extra in catalog.Extra.Where(extra => extra.IsRequired))
        {
            if (extra.IsGenre && extra.Options.Count > 0)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Catalog rows such as Cinemeta's "New" require a year in the genre slot.
    /// Pick the current year when offered, otherwise the first option.
    /// </summary>
    private static string? ResolveGenre(AddonCatalog catalog, out string? subtitle)
    {
        subtitle = null;
        var required = catalog.RequiredGenre;
        if (required is null)
        {
            return null;
        }

        var currentYear = DateTime.Now.Year.ToString(CultureInfo.InvariantCulture);
        var value = required.Options.FirstOrDefault(option => option == currentYear)
            ?? required.Options[0];

        subtitle = value;
        return value;
    }

    private static string BuildCatalogTitle(AddonCatalog catalog)
    {
        var typePlural = TypePlural(catalog.Type);
        var name = string.IsNullOrWhiteSpace(catalog.Name) ? typePlural : catalog.Name.Trim();

        if (name.Contains(typePlural, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(catalog.Type, StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        return $"{name} {typePlural}";
    }

    private static string TypePlural(string type) => type.ToLowerInvariant() switch
    {
        "movie" => "Movies",
        "series" => "Series",
        "tv" or "channel" => "Live TV",
        "music" => "Music",
        "podcast" => "Podcasts",
        _ => string.IsNullOrWhiteSpace(type)
            ? "Catalog"
            : char.ToUpper(type[0], CultureInfo.InvariantCulture) + type[1..] + "s",
    };

    private static string Describe(Exception exception) => exception switch
    {
        StremioException => exception.Message,
        HttpRequestException => "Couldn't reach the addon. Check your connection and try again.",
        TaskCanceledException => "The addon took too long to respond.",
        _ => "Something went wrong while loading your catalog.",
    };

    private sealed record CatalogRef(AddonEntry Entry, AddonCatalog Catalog, int Order);
}

/// <summary>A section of the home feed backed by one addon catalog.</summary>
public sealed class CatalogRowViewModel(
    int order,
    string title,
    string? subtitle,
    string addonName,
    IReadOnlyList<MetaItem> items) : ObservableObject
{
    public int Order { get; } = order;

    public string Title { get; } = title;

    public string? Subtitle { get; } = subtitle;

    public string AddonName { get; } = addonName;

    public IReadOnlyList<MetaItem> Items { get; } = items;

    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);

    public string CountLabel => Items.Count == 1 ? "1 title" : $"{Items.Count} titles";
}

/// <summary>One slide of the Top 10 carousel.</summary>
public sealed class HeroItemViewModel(MetaItem meta, int rank, string sourceLine) : ObservableObject
{
    public MetaItem Meta { get; } = meta;

    public int Rank { get; } = rank;

    public string RankText { get; } = rank.ToString(CultureInfo.InvariantCulture);

    public string SourceLine { get; } = sourceLine;

    public string Title => Meta.Name;

    public string MetaLine => Meta.HeroMetaLine;

    public string Description => Meta.Description ?? "";

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public string RatingText => Meta.RatingText;

    public bool HasRating => Meta.HasRating;

    public BitmapImage? BackgroundImage => Meta.BackgroundImage;

    public BitmapImage? LogoImage => Meta.LogoImage;

    public bool HasLogo => Meta.HasLogo;

    public bool NoLogo => !HasLogo;

    public override string ToString() => $"{Title}, ranked {Rank}";
}

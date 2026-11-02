using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Pirsa.Models;
using Pirsa.Services;

namespace Pirsa.ViewModels;

/// <summary>
/// Details for one movie or series, including fact sheet and season/episode data.
/// </summary>
public partial class DetailsViewModel : ObservableObject
{
    private readonly AddonRegistry _registry;
    private readonly WatchProgressStore _watchProgress;
    private readonly WatchlistStore _watchlist;
    private string _type = "";
    private string _id = "";

    public DetailsViewModel(
        AddonRegistry registry,
        WatchProgressStore watchProgress,
        WatchlistStore watchlist)
    {
        _registry = registry;
        _watchProgress = watchProgress;
        _watchlist = watchlist;
        ErrorMessage = "";
    }

    public ObservableCollection<MetaFact> Facts { get; } = [];

    public ObservableCollection<SeasonOption> Seasons { get; } = [];

    public ObservableCollection<MetaVideo> Episodes { get; } = [];

    [ObservableProperty]
    public partial MetaItem? Meta { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    [ObservableProperty]
    public partial SeasonOption? SelectedSeason { get; set; }

    /// <summary>Whether this title is saved to the local watchlist.</summary>
    [ObservableProperty]
    public partial bool IsInWatchlist { get; set; }

    public string WatchlistButtonText => IsInWatchlist ? "In watchlist" : "Add to watchlist";

    /// <summary>Add glyph when the title can be saved, checkmark once it is.</summary>
    public string WatchlistGlyph => IsInWatchlist ? "\uE73E" : "\uE710";

    public string WatchlistAutomationName => IsInWatchlist
        ? "Remove from watchlist"
        : "Add to watchlist";

    /// <summary>
    /// The newest unfinished movie or episode for this title, from the local
    /// watch history. Drives the resume label on the Play button.
    /// </summary>
    public WatchProgressEntry? ResumeEntry { get; private set; }

    public bool HasResume => ResumeEntry is not null;

    public string PlayButtonText => ResumeEntry switch
    {
        null => "Play",
        { IsSeries: true } entry => $"Resume {entry.EpisodeTag}",
        { } entry => $"Resume {WatchProgressEntry.FormatPosition(entry.PositionMs)}",
    };

    public string Title => Meta?.Name ?? "";

    public string TypeLabel => Meta?.TypeLabel ?? "Title";

    public string MetaLine => Meta?.HeroMetaLine ?? "";

    public string Description => Meta?.Description ?? "";

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public string RatingText => Meta?.RatingText ?? "";

    public bool HasRating => Meta?.HasRating ?? false;

    public BitmapImage? PosterImage => Meta?.PosterDetailImage;

    public BitmapImage? BackdropImage => Meta?.BackgroundImage;

    public BitmapImage? LogoImage => Meta?.LogoImage;

    public bool HasLogo => Meta?.HasLogo ?? false;

    public bool NoLogo => !HasLogo;

    public bool HasPoster => Meta?.HasPoster ?? false;

    public bool NoPoster => !HasPoster;

    public bool HasFacts => Facts.Count > 0;

    public bool HasSeasonPicker => Seasons.Count > 1;

    public bool HasSeasons => Seasons.Count > 0;

    public bool HasEpisodes => Episodes.Count > 0;

    public bool ShowLoading => IsLoading && Meta is null;

    public bool ShowErrorState => HasError && Meta is null;

    public bool ShowWarning => HasError && Meta is not null;

    public string ImdbUrl =>
        Meta?.Id is { Length: > 0 } id && id.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
            ? $"https://www.imdb.com/title/{id}/"
            : "";

    public bool HasImdb => !string.IsNullOrEmpty(ImdbUrl);

    /// <summary>
    /// Loads full meta data. A preview passed in by the catalog page can already
    /// be on screen, so failures degrade to a warning instead of a blank page.
    /// </summary>
    public async Task LoadAsync(string type, string id, CancellationToken cancellationToken = default)
    {
        _type = type;
        _id = id;

        if (string.IsNullOrWhiteSpace(_type))
        {
            _type = Meta?.Type ?? "";
        }

        if (string.IsNullOrWhiteSpace(_id))
        {
            _id = Meta?.Id ?? "";
        }

        IsLoading = true;
        HasError = false;
        ErrorMessage = "";

        try
        {
            // The button state only needs local storage, so show it before
            // waiting on the network.
            await RefreshWatchlistStateAsync(cancellationToken);

            await _registry.EnsureLoadedAsync(cancellationToken);

            var sourceUrl = Meta?.SourceAddonUrl;
            var client = _registry.GetClient(sourceUrl)
                ?? _registry.Entries.FirstOrDefault()?.Client;

            if (client is null)
            {
                HasError = true;
                ErrorMessage = "No addons are configured. Add one in Settings → Addons.";
                return;
            }

            var meta = await client.GetMetaAsync(_type, _id, cancellationToken);
            if (string.IsNullOrWhiteSpace(meta.SourceAddonUrl))
            {
                meta.SourceAddonUrl = sourceUrl ?? client.ManifestUrl;
            }

            Meta = meta;
            BuildFacts(meta);
            BuildSeasons(meta);
            await LoadWatchProgressAsync(meta, cancellationToken);
            UpdateWatchlistState();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = Describe(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task ReloadAsync(CancellationToken cancellationToken) =>
        LoadAsync(_type, _id, cancellationToken);

    /// <summary>
    /// Saves or unsaves the current title. The button state follows the store,
    /// so a failed write leaves the label unchanged.
    /// </summary>
    [RelayCommand]
    private async Task ToggleWatchlistAsync(CancellationToken cancellationToken)
    {
        if (Meta is not { } meta || string.IsNullOrWhiteSpace(meta.Id))
        {
            return;
        }

        try
        {
            IsInWatchlist = await _watchlist.ToggleAsync(meta, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Keep the previous state; the title simply wasn't saved.
        }
    }

    private async Task RefreshWatchlistStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _watchlist.EnsureLoadedAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return;
        }

        UpdateWatchlistState();
    }

    private void UpdateWatchlistState() =>
        IsInWatchlist = Meta is { } meta && _watchlist.Contains(meta.Id);

    private async Task LoadWatchProgressAsync(MetaItem meta, CancellationToken cancellationToken)
    {
        try
        {
            await _watchProgress.EnsureLoadedAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return;
        }

        var entries = _watchProgress.GetEpisodeEntries(meta.Id);
        ResumeEntry = _watchProgress.FindResume(meta.Id);

        foreach (var video in meta.Videos ?? [])
        {
            if (entries.TryGetValue(
                    WatchProgressStore.EpisodeKey(video.Season, video.EpisodeNumber),
                    out var entry))
            {
                video.WatchProgress = entry.Completed ? 1 : entry.ProgressFraction;
                video.WatchLabel = entry.ResumeLabel;
            }
            else
            {
                video.WatchProgress = 0;
                video.WatchLabel = "";
            }
        }

        OnPropertyChanged(nameof(ResumeEntry));
        OnPropertyChanged(nameof(HasResume));
        OnPropertyChanged(nameof(PlayButtonText));
    }

    private void BuildFacts(MetaItem meta)
    {
        Facts.Clear();

        Add("Director", Join(meta.Director, 3));
        Add("Writers", Join(meta.Writer, 4));
        Add("Cast", Join(meta.Cast, 10));
        Add("Country", meta.Country);
        Add("Awards", meta.Awards);
        Add("Status", meta.Status);
        Add("Released", FormatDate(meta.Released));

        OnPropertyChanged(nameof(HasFacts));

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Facts.Add(new MetaFact(label, value.Trim()));
            }
        }
    }

    private void BuildSeasons(MetaItem meta)
    {
        Seasons.Clear();
        Episodes.Clear();
        SelectedSeason = null;

        if (meta.Videos is not { Count: > 0 })
        {
            OnPropertyChanged(nameof(HasSeasons));
            OnPropertyChanged(nameof(HasSeasonPicker));
            OnPropertyChanged(nameof(HasEpisodes));
            return;
        }

        var groups = meta.Videos
            .Where(video => video.Season >= 0)
            .GroupBy(video => video.Season)
            .OrderBy(group => group.Key == 0 ? int.MaxValue : group.Key)
            .ThenBy(group => group.Key)
            .Select(group => new SeasonOption(
                group.Key,
                group.OrderBy(video => video.EpisodeNumber).ToList()))
            .ToList();

        foreach (var season in groups)
        {
            Seasons.Add(season);
        }

        OnPropertyChanged(nameof(HasSeasons));
        OnPropertyChanged(nameof(HasSeasonPicker));

        SelectedSeason =
            Seasons.FirstOrDefault(season => season.Number == 1)
            ?? Seasons.FirstOrDefault(season => season.Number > 0)
            ?? Seasons.FirstOrDefault();
    }

    partial void OnIsInWatchlistChanged(bool value)
    {
        OnPropertyChanged(nameof(WatchlistButtonText));
        OnPropertyChanged(nameof(WatchlistGlyph));
        OnPropertyChanged(nameof(WatchlistAutomationName));
    }

    partial void OnMetaChanged(MetaItem? value)
    {
        UpdateWatchlistState();

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(MetaLine));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(RatingText));
        OnPropertyChanged(nameof(HasRating));
        OnPropertyChanged(nameof(PosterImage));
        OnPropertyChanged(nameof(BackdropImage));
        OnPropertyChanged(nameof(LogoImage));
        OnPropertyChanged(nameof(HasLogo));
        OnPropertyChanged(nameof(NoLogo));
        OnPropertyChanged(nameof(HasPoster));
        OnPropertyChanged(nameof(NoPoster));
        OnPropertyChanged(nameof(ImdbUrl));
        OnPropertyChanged(nameof(HasImdb));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowErrorState));
        OnPropertyChanged(nameof(ShowWarning));
    }

    partial void OnSelectedSeasonChanged(SeasonOption? value)
    {
        Episodes.Clear();
        if (value is not null)
        {
            foreach (var episode in value.Episodes)
            {
                Episodes.Add(episode);
            }
        }

        OnPropertyChanged(nameof(HasEpisodes));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLoading));
    }

    partial void OnHasErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowErrorState));
        OnPropertyChanged(nameof(ShowWarning));
    }

    private static string Join(IEnumerable<string>? values, int limit)
    {
        var items = values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(limit)
            .ToList();
        return items is { Count: > 0 } ? string.Join(", ", items) : "";
    }

    private static string FormatDate(string? value) =>
        DateTimeOffset.TryParse(value, out var date)
            ? date.ToString("MMMM d, yyyy")
            : "";

    private static string Describe(Exception exception) => exception switch
    {
        StremioException => exception.Message,
        HttpRequestException => "Couldn't reach the addon. Check your connection and try again.",
        TaskCanceledException => "The addon took too long to respond.",
        _ => "Something went wrong while loading this title.",
    };
}

/// <summary>A labeled fact in the details fact sheet.</summary>
public sealed class MetaFact(string label, string value) : ObservableObject
{
    public string Label { get; } = label;

    public string Value { get; } = value;
}

/// <summary>A season entry in the season picker.</summary>
public sealed class SeasonOption(int number, IReadOnlyList<MetaVideo> episodes) : ObservableObject
{
    public int Number { get; } = number;

    public string Label { get; } = number == 0 ? "Specials" : $"Season {number}";

    public IReadOnlyList<MetaVideo> Episodes { get; } = episodes;
}

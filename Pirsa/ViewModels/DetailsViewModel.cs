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
    private readonly EventHandler _registryChangedHandler;
    private string _type = "";
    private string _id = "";
    private bool _streamAddonsKnown;

    public DetailsViewModel(
        AddonRegistry registry,
        WatchProgressStore watchProgress,
        WatchlistStore watchlist)
    {
        _registry = registry;
        _watchProgress = watchProgress;
        _watchlist = watchlist;
        ErrorMessage = "";

        _registryChangedHandler = (_, _) => RaiseStreamAddonInfo();
        Attach();
    }

    /// <summary>Subscribes to the shared addon list (also after a cached navigation).</summary>
    public void Attach()
    {
        _registry.Changed -= _registryChangedHandler;
        _registry.Changed += _registryChangedHandler;
    }

    /// <summary>Stops listening to the shared addon list when the page goes away.</summary>
    public void Detach() => _registry.Changed -= _registryChangedHandler;

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

    // ----- Watched state ------------------------------------------------

    /// <summary>Aggregate state for this movie or series.</summary>
    public TitleWatchSummary WatchSummary { get; private set; }

    public bool IsSeries => Meta is { } meta && IsSeriesType(meta.Type);

    public bool HasWatchState => WatchSummary.State != TitleWatchState.None;

    public bool IsTitleWatched => WatchSummary.IsWatched;

    public string WatchStateText => WatchSummary.BadgeText;

    /// <summary>Menu button label. Series only count as watched when complete.</summary>
    public string WatchedButtonText => WatchSummary.State switch
    {
        TitleWatchState.Watched => "Watched",
        TitleWatchState.PartiallyWatched => "Partially watched",
        TitleWatchState.InProgress => "Watching",
        _ => "Mark as watched",
    };

    /// <summary>Checkmark once anything is watched, radar otherwise.</summary>
    public string WatchedGlyph => HasWatchState ? "" : "";

    public string WatchedAutomationName => HasWatchState
        ? "Watched options"
        : "Mark as watched";

    public string SeasonWatchMenuText => SelectedSeason is { } season
        ? $"Mark {season.Label} as watched"
        : "Mark this season as watched";

    public string SeasonUnwatchMenuText => SelectedSeason is { } season
        ? $"Mark {season.Label} as unwatched"
        : "Mark this season as unwatched";

    [ObservableProperty]
    public partial bool IsSelectingEpisodes { get; set; }

    public bool CanSelectEpisodes => Meta?.Videos is { Count: > 0 };

    public string SelectEpisodesButtonText => IsSelectingEpisodes ? "Done" : "Select episodes";

    public int SelectedEpisodeCount =>
        Meta?.Videos?.Count(video => video.IsSelected) ?? 0;

    public bool HasSelectedEpisodes => SelectedEpisodeCount > 0;

    public string SelectionSummaryText => SelectedEpisodeCount switch
    {
        0 => "Select episodes below, then mark them watched or unwatched.",
        1 => "1 episode selected",
        var count => $"{count} episodes selected",
    };

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

    /// <summary>True once the enabled addon manifests have been inspected.</summary>
    public bool StreamAddonsKnown => _streamAddonsKnown;

    /// <summary>At least one enabled addon declares a stream resource.</summary>
    public bool HasEnabledStreamAddon =>
        _registry.EnabledEntries.Any(entry => entry.HasStreams);

    /// <summary>A configured stream addon exists but is turned off.</summary>
    public bool HasDisabledStreamAddon =>
        !HasEnabledStreamAddon &&
        _registry.Entries.Any(entry => !entry.IsEnabled && entry.HasStreams);

    /// <summary>Shows the setup hint once we know there is nothing to play with.</summary>
    public bool HasStreamAddonHint => _streamAddonsKnown && !HasEnabledStreamAddon;

    public string StreamAddonHintText => HasDisabledStreamAddon
        ? "Your stream addon is turned off, so this title can't find sources."
        : "No stream addon is enabled. Addons are third-party — only add ones you trust.";

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
            await _registry.LoadManifestsAsync(cancellationToken);

            _streamAddonsKnown = true;
            RaiseStreamAddonInfo();

            var sourceUrl = Meta?.SourceAddonUrl;

            // Meta follows the addon priority order. Custom id prefixes only
            // match the addon that declared them, so an addon that knows a
            // language (or a private id) is picked without breaking others.
            var candidates = _registry.MetaCandidates(_type, _id, sourceUrl);

            if (candidates.Count == 0)
            {
                // Addons that don't declare their meta resource can still
                // answer: try the catalog's addon, then the first enabled one.
                var fallback = _registry.GetEnabledEntry(sourceUrl)
                    ?? _registry.EnabledEntries.FirstOrDefault();

                if (fallback is not null)
                {
                    candidates = [fallback];
                }
            }

            if (candidates.Count == 0)
            {
                HasError = true;
                ErrorMessage = "No addon provides metadata. Turn one on in Settings → Addons.";
                return;
            }

            MetaItem? meta = null;
            Exception? lastError = null;

            foreach (var candidate in candidates)
            {
                try
                {
                    meta = await candidate.Client.GetMetaAsync(_type, _id, cancellationToken);

                    // A series without episodes cannot be played; give the
                    // next addon in the priority list a turn.
                    if (NeedsEpisodes(_type) && meta.Videos is not { Count: > 0 } &&
                        candidates.Count > 1)
                    {
                        lastError = new StremioException($"{candidate.Name} has no episode list.");
                        meta = null;
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(meta.SourceAddonUrl))
                    {
                        meta.SourceAddonUrl = candidate.ManifestUrl;
                    }

                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                }
            }

            if (meta is null)
            {
                throw lastError ?? new StremioException("No addon knew this title.");
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

    private void RaiseStreamAddonInfo()
    {
        OnPropertyChanged(nameof(StreamAddonsKnown));
        OnPropertyChanged(nameof(HasEnabledStreamAddon));
        OnPropertyChanged(nameof(HasDisabledStreamAddon));
        OnPropertyChanged(nameof(HasStreamAddonHint));
        OnPropertyChanged(nameof(StreamAddonHintText));
    }

    private static bool NeedsEpisodes(string type) =>
        type.Equals("series", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("tv", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Marks the title watched or unwatched from the hero menu. For a series
    /// this means every episode the addon listed; episodes can also be marked
    /// one by one or as a selection below.
    /// </summary>
    [RelayCommand]
    private Task MarkWatchedAsync(CancellationToken cancellationToken) =>
        SetTitleWatchedAsync(true, cancellationToken);

    [RelayCommand]
    private Task MarkUnwatchedAsync(CancellationToken cancellationToken) =>
        SetTitleWatchedAsync(false, cancellationToken);

    [RelayCommand]
    private Task MarkSeasonWatchedAsync(CancellationToken cancellationToken) =>
        SetSeasonWatchedAsync(true, cancellationToken);

    [RelayCommand]
    private Task MarkSeasonUnwatchedAsync(CancellationToken cancellationToken) =>
        SetSeasonWatchedAsync(false, cancellationToken);

    [RelayCommand]
    private Task MarkSelectedWatchedAsync(CancellationToken cancellationToken) =>
        SetSelectedWatchedAsync(true, cancellationToken);

    [RelayCommand]
    private Task MarkSelectedUnwatchedAsync(CancellationToken cancellationToken) =>
        SetSelectedWatchedAsync(false, cancellationToken);

    [RelayCommand]
    private void ToggleSelectEpisodes() => IsSelectingEpisodes = !IsSelectingEpisodes;

    [RelayCommand]
    private void SelectCurrentSeason()
    {
        foreach (var episode in SelectedSeason?.Episodes ?? [])
        {
            episode.IsSelected = true;
        }

        NotifySelectionChanged();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var video in Meta?.Videos ?? [])
        {
            video.IsSelected = false;
        }

        NotifySelectionChanged();
    }

    public async Task SetTitleWatchedAsync(bool watched, CancellationToken cancellationToken = default)
    {
        if (Meta is not { } meta)
        {
            return;
        }

        try
        {
            if (IsSeries)
            {
                var episodes = meta.Videos?.Where(video => video.EpisodeNumber > 0).ToList() ?? [];
                if (episodes.Count == 0)
                {
                    return;
                }

                await _watchProgress.SetEpisodesWatchedAsync(meta, episodes, watched, cancellationToken);
            }
            else
            {
                await _watchProgress.SetMovieWatchedAsync(meta, watched, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            return;
        }

        await RefreshWatchStateAsync(meta, cancellationToken);
    }

    public async Task SetEpisodeWatchedAsync(
        MetaVideo episode,
        bool watched,
        CancellationToken cancellationToken = default)
    {
        if (Meta is not { } meta)
        {
            return;
        }

        try
        {
            await _watchProgress.SetEpisodeWatchedAsync(meta, episode, watched, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            return;
        }

        await RefreshWatchStateAsync(meta, cancellationToken);
    }

    public bool IsEpisodeWatched(MetaVideo episode) =>
        Meta is { } meta && _watchProgress.IsWatched(PlaybackRequest.ForEpisode(meta, episode).VideoId);

    public async Task SetSeasonWatchedAsync(bool watched, CancellationToken cancellationToken = default)
    {
        if (Meta is not { } meta || SelectedSeason is not { } season)
        {
            return;
        }

        try
        {
            await _watchProgress.SetEpisodesWatchedAsync(
                meta,
                season.Episodes,
                watched,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            return;
        }

        await RefreshWatchStateAsync(meta, cancellationToken);
    }

    public async Task SetSelectedWatchedAsync(bool watched, CancellationToken cancellationToken = default)
    {
        if (Meta is not { } meta)
        {
            return;
        }

        var selected = meta.Videos?.Where(video => video.IsSelected).ToList() ?? [];
        if (selected.Count == 0)
        {
            return;
        }

        try
        {
            await _watchProgress.SetEpisodesWatchedAsync(meta, selected, watched, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            return;
        }

        foreach (var episode in selected)
        {
            episode.IsSelected = false;
        }

        NotifySelectionChanged();
        await RefreshWatchStateAsync(meta, cancellationToken);
    }

    /// <summary>Called by the checkboxes so the selection toolbar can update.</summary>
    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedEpisodeCount));
        OnPropertyChanged(nameof(HasSelectedEpisodes));
        OnPropertyChanged(nameof(SelectionSummaryText));
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
            await App.Settings.EnsureLoadedAsync(cancellationToken);
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

        // Old history rows have no episode count; a series finished before the
        // watched feature existed would otherwise never read as "Watched".
        var episodeCount = meta.Videos?.Count(video => video.EpisodeNumber > 0) ?? 0;
        if (episodeCount > 0)
        {
            await _watchProgress.RememberEpisodeCountAsync(meta.Id, episodeCount, cancellationToken);
        }

        ApplyWatchState(meta);
    }

    private async Task RefreshWatchStateAsync(MetaItem meta, CancellationToken cancellationToken)
    {
        try
        {
            await App.Settings.EnsureLoadedAsync(cancellationToken);
            await _watchProgress.EnsureLoadedAsync(cancellationToken);
            await _watchProgress.ReconcileNextUpAsync(meta, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            return;
        }

        ApplyWatchState(meta);
    }

    private void ApplyWatchState(MetaItem meta)
    {
        var entries = _watchProgress.GetEpisodeEntries(meta.Id);
        var resumeEnabled = App.Settings.ResumePlayback;
        ResumeEntry = resumeEnabled ? _watchProgress.FindResume(meta.Id) : null;

        foreach (var video in meta.Videos ?? [])
        {
            if (entries.TryGetValue(
                    WatchProgressStore.EpisodeKey(video.Season, video.EpisodeNumber),
                    out var entry))
            {
                video.IsWatched = entry.IsWatched;
                video.WatchProgress = entry.Completed ? 1 : entry.ProgressFraction;
                video.WatchLabel = entry.Completed
                    ? "Watched"
                    : resumeEnabled ? entry.ResumeLabel : "";
            }
            else
            {
                video.IsWatched = false;
                video.WatchProgress = 0;
                video.WatchLabel = "";
            }

            video.ShowSelection = IsSelectingEpisodes;
        }

        WatchSummary = _watchProgress.GetTitleState(meta.Id, meta.Type);

        foreach (var season in Seasons)
        {
            season.UpdateWatchState(
                season.Episodes.Count(video => video.IsWatched),
                season.Episodes.Count);
        }

        OnPropertyChanged(nameof(ResumeEntry));
        OnPropertyChanged(nameof(HasResume));
        OnPropertyChanged(nameof(PlayButtonText));
        RaiseWatchState();
        NotifySelectionChanged();
    }

    private void RaiseWatchState()
    {
        OnPropertyChanged(nameof(WatchSummary));
        OnPropertyChanged(nameof(IsSeries));
        OnPropertyChanged(nameof(HasWatchState));
        OnPropertyChanged(nameof(IsTitleWatched));
        OnPropertyChanged(nameof(WatchStateText));
        OnPropertyChanged(nameof(WatchedButtonText));
        OnPropertyChanged(nameof(WatchedGlyph));
        OnPropertyChanged(nameof(WatchedAutomationName));
        OnPropertyChanged(nameof(SeasonWatchMenuText));
        OnPropertyChanged(nameof(SeasonUnwatchMenuText));
        OnPropertyChanged(nameof(CanSelectEpisodes));
    }

    private static bool IsSeriesType(string? type) =>
        type is not null &&
        (type.Equals("series", StringComparison.OrdinalIgnoreCase) ||
         type.Equals("tv", StringComparison.OrdinalIgnoreCase));

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
        IsSelectingEpisodes = false;
        WatchSummary = default;
        RaiseWatchState();

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
        OnPropertyChanged(nameof(SeasonWatchMenuText));
        OnPropertyChanged(nameof(SeasonUnwatchMenuText));
    }

    partial void OnIsSelectingEpisodesChanged(bool value)
    {
        foreach (var video in Meta?.Videos ?? [])
        {
            video.ShowSelection = value;

            if (!value)
            {
                video.IsSelected = false;
            }
        }

        OnPropertyChanged(nameof(SelectEpisodesButtonText));
        NotifySelectionChanged();
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

/// <summary>A season entry in the season picker, with its watch progress.</summary>
public sealed class SeasonOption(int number, IReadOnlyList<MetaVideo> episodes) : ObservableObject
{
    private int _watchedCount;
    private int _episodeCount;

    public int Number { get; } = number;

    public string Label { get; } = number == 0 ? "Specials" : $"Season {number}";

    public IReadOnlyList<MetaVideo> Episodes { get; } = episodes;

    public int WatchedCount => _watchedCount;

    public int EpisodeCount => _episodeCount;

    public bool IsWatched => _episodeCount > 0 && _watchedCount >= _episodeCount;

    public string WatchStatusText => _watchedCount == 0
        ? ""
        : IsWatched
            ? "Watched"
            : $"{_watchedCount}/{_episodeCount}";

    /// <summary>Season label with a checkmark once every episode is watched.</summary>
    public string DisplayLabel => IsWatched ? $"{Label} ✓" : Label;

    internal void UpdateWatchState(int watchedCount, int episodeCount)
    {
        _watchedCount = watchedCount;
        _episodeCount = episodeCount;
        OnPropertyChanged(nameof(WatchedCount));
        OnPropertyChanged(nameof(EpisodeCount));
        OnPropertyChanged(nameof(IsWatched));
        OnPropertyChanged(nameof(WatchStatusText));
        OnPropertyChanged(nameof(DisplayLabel));
    }
}

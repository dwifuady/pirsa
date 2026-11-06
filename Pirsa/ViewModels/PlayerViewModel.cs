using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Pirsa.Helpers;
using Pirsa.Models;
using Pirsa.Services;

namespace Pirsa.ViewModels;

/// <summary>
/// Discovers playable sources from the configured stream addons and keeps the
/// player's source picker in sync. Playback itself lives in the page (VLC).
/// </summary>
public partial class PlayerViewModel : ObservableObject
{
    private readonly AddonRegistry _registry;
    private readonly StreamDiscovery _streamDiscovery;
    private readonly WatchProgressStore _watchProgress;
    private readonly List<AddonStreamResult> _results = [];
    private readonly EventHandler _registryChangedHandler;
    private CancellationTokenSource? _loadCts;
    private Task _fillTask = Task.CompletedTask;
    private int _loadVersion;
    private IReadOnlyList<SourceTab> _sourceTabs = [];

    private const int MaxSourceTabTitleLength = 22;

    public PlayerViewModel(
        AddonRegistry registry,
        StreamDiscovery streamDiscovery,
        WatchProgressStore watchProgress)
    {
        _registry = registry;
        _streamDiscovery = streamDiscovery;
        _watchProgress = watchProgress;
        ErrorMessage = "";
        StatusText = "";
        SubtitleStatus = "";

        OffSubtitle = new SubtitleLanguageChoice(
            "Off",
            "",
            isOff: true,
            [],
            "No subtitles");
        SubtitleMenu.Add(OffSubtitle);

        // Reordering or disabling an addon re-ranks the open source list.
        _registryChangedHandler = (_, _) =>
        {
            if (Sources.Count > 0)
            {
                RebuildSources();
            }
        };
        _registry.Changed += _registryChangedHandler;
    }

    /// <summary>Stops listening to the shared addon list when the page goes away.</summary>
    public void Detach()
    {
        _registry.Changed -= _registryChangedHandler;
        _loadVersion++;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    public ObservableCollection<PlaybackSource> Sources { get; } = [];

    /// <summary>
    /// Picker tabs: "All" first, then one per addon that returned a source, in
    /// addon order. Replaced as a whole so the page rebuilds the bar once.
    /// </summary>
    public IReadOnlyList<SourceTab> SourceTabs => _sourceTabs;

    /// <summary>Rows for the selected tab.</summary>
    public ObservableCollection<PlaybackSource> VisibleSources { get; } = [];

    /// <summary>
    /// All + at least two addons. With one addon the "All" tab would just
    /// repeat it, so the picker hides the tab bar.
    /// </summary>
    public bool HasMultipleSourceTabs => _sourceTabs.Count > 2;

    /// <summary>Off + one entry per available language.</summary>
    public ObservableCollection<SubtitleLanguageChoice> SubtitleMenu { get; } = [];

    /// <summary>Seasons of the series currently playing, for the in-player list.</summary>
    public ObservableCollection<PlayerSeason> Seasons { get; } = [];

    /// <summary>Episodes of <see cref="SelectedSeason"/>.</summary>
    public ObservableCollection<PlayerEpisode> Episodes { get; } = [];

    public SubtitleLanguageChoice OffSubtitle { get; }

    [ObservableProperty]
    public partial PlaybackRequest? Request { get; set; }

    /// <summary>
    /// The source the user is on, or null before anything has been played. Also
    /// the one held across <see cref="RebuildSources"/> when late addon answers
    /// arrive under the picker.
    /// </summary>
    [ObservableProperty]
    public partial PlaybackSource? SelectedSource { get; set; }

    [ObservableProperty]
    public partial SourceTab? SelectedSourceTab { get; set; }

    partial void OnSelectedSourceTabChanged(SourceTab? value) => RefreshVisibleSources();

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingSubtitles { get; set; }

    [ObservableProperty]
    public partial string SubtitleStatus { get; set; }

    [ObservableProperty]
    public partial SubtitleVersionChoice? SelectedSubtitle { get; set; }

    /// <summary>
    /// The episode that follows the current one, when the player can find it.
    /// Drives the Up next card and episode auto-advance.
    /// </summary>
    [ObservableProperty]
    public partial MetaVideo? NextEpisode { get; set; }

    /// <summary>Season shown in the in-player episode list.</summary>
    [ObservableProperty]
    public partial PlayerSeason? SelectedSeason { get; set; }

    /// <summary>True once the series metadata exposed at least one season.</summary>
    [ObservableProperty]
    public partial bool CanBrowseEpisodes { get; set; }

    public bool HasNextEpisode => NextEpisode is not null;

    public string NextEpisodeTag => NextEpisode is null
        ? ""
        : $"S{NextEpisode.Season:00}E{NextEpisode.EpisodeNumber:00}";

    public string NextEpisodeLine => NextEpisode is null
        ? ""
        : $"{NextEpisodeTag} · {NextEpisode.Title}";

    public BitmapImage? NextEpisodeImage => NextEpisode?.ThumbnailImage;

    partial void OnNextEpisodeChanged(MetaVideo? value)
    {
        OnPropertyChanged(nameof(HasNextEpisode));
        OnPropertyChanged(nameof(NextEpisodeTag));
        OnPropertyChanged(nameof(NextEpisodeLine));
        OnPropertyChanged(nameof(NextEpisodeImage));
    }

    public bool HasSubtitles => SubtitleMenu.Count > 1;

    public string Title => Request?.Title ?? "";

    public string Subtitle => Request?.Subtitle ?? "";

    public BitmapImage? BackdropImage => Request?.Meta.BackgroundImage;

    public BitmapImage? PosterImage => Request?.Meta.PosterDetailImage;

    /// <summary>
    /// Artwork behind the startup / source-discovery overlay. The poster is the
    /// title's most recognisable art, so it leads; the backdrop is only a
    /// fallback for titles that have no poster.
    /// </summary>
    public BitmapImage? LoadingArtworkImage => PosterImage ?? BackdropImage;

    public BitmapImage? LogoImage => Request?.Meta.LogoImage;

    public bool HasLogo => Request?.Meta.HasLogo ?? false;

    public bool NoLogo => !HasLogo;

    public int SourceCount => Sources.Count;

    public bool HasSources => Sources.Count > 0;

    public string SourcesLabel => SourceCount switch
    {
        0 => "Sources",
        1 => "1 source",
        _ => $"{SourceCount} sources",
    };

    /// <summary>
    /// The transport-bar source button: the quality actually playing, like a
    /// resolution picker in a web player, and a plain label before that.
    /// </summary>
    public string SourceButtonLabel => SelectedSource is { QualityLabel: var quality }
        && !string.Equals(quality, "Unknown", StringComparison.Ordinal)
            ? quality
            : "Sources";

    /// <summary>The picker heading: a choice to make, or the list to switch from.</summary>
    public string SourceFlyoutTitle => SelectedSource is null ? "Choose a source" : "Source";

    /// <summary>The picker subheading: how many streams, and from how many addons.</summary>
    public string SourceFlyoutDetail
    {
        get
        {
            if (SourceCount == 0)
            {
                return IsDiscovering ? "Finding sources…" : "No sources";
            }

            // A tab narrows the list, so say how much of the total it is.
            if (SelectedSourceTab is { Key: not null } tab)
            {
                return $"{tab.Count} of {SourceCount} streams";
            }

            var streams = SourceCount == 1 ? "1 stream" : $"{SourceCount} streams";
            var addons = Sources
                .Select(source => source.AddonName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            return addons > 1 ? $"{streams} · {addons} addons" : streams;
        }
    }

    partial void OnSelectedSourceChanged(PlaybackSource? value)
    {
        OnPropertyChanged(nameof(SourceButtonLabel));
        OnPropertyChanged(nameof(SourceFlyoutTitle));
    }

    /// <summary>
    /// True while addons are still answering for the current request.
    /// </summary>
    public bool IsDiscovering => !_fillTask.IsCompleted;

    /// <summary>
    /// Loads streams from the enabled addons in priority order and returns as
    /// soon as the first playable source arrives. The remaining addons keep
    /// filling the picker in the background, so one slow addon never delays
    /// playback.
    /// </summary>
    public async Task<bool> LoadAsync(PlaybackRequest request, CancellationToken cancellationToken = default)
    {
        Request = request;
        SelectedSource = null;
        Sources.Clear();
        ClearSourceTabs();
        _results.Clear();
        HasError = false;
        ErrorMessage = "";
        StatusText = "Finding sources…";
        IsLoading = true;
        RaiseSourceInfo();

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _loadCts.Token;
        var version = ++_loadVersion;

        try
        {
            await _registry.EnsureLoadedAsync(token);

            if (_registry.EnabledEntries.Count == 0)
            {
                HasError = true;
                ErrorMessage = "No addons are enabled. Turn one on in Settings → Addons.";
                StatusText = "No sources";
                IsLoading = false;
                return false;
            }

            // The details page warms this pass, so results are often already
            // waiting when Play is pressed.
            var session = _streamDiscovery.Start(request);
            var firstSource = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _fillTask = FillAsync(session, firstSource, version, token);
            return await firstSource.Task.WaitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            if (version == _loadVersion)
            {
                HasError = true;
                ErrorMessage = $"Couldn't find a source. {exception.Message}";
                StatusText = "No sources";
                IsLoading = false;
            }

            return false;
        }
    }

    /// <summary>Waits until every addon has answered for the current request.</summary>
    public Task WaitForSourcesAsync(CancellationToken cancellationToken = default) =>
        _fillTask.WaitAsync(cancellationToken);

    // -------------------------------------------------------------- episodes

    /// <summary>
    /// Builds the in-player season and episode list from full series metadata.
    /// A Continue watching request carries only a stub meta, so the player
    /// resolves the series first and hands the result here.
    /// </summary>
    public async Task SetSeriesAsync(
        MetaItem series,
        PlaybackRequest request,
        CancellationToken cancellationToken = default)
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
            // The list still works without local watch markers.
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        BuildEpisodes(series, request);
    }

    /// <summary>Highlights the episode currently playing in every season list.</summary>
    public void MarkCurrentEpisode(int season, int episode)
    {
        foreach (var item in Seasons.SelectMany(entry => entry.Episodes))
        {
            item.IsCurrent = item.Season == season && item.EpisodeNumber == episode;
        }
    }

    /// <summary>Drops the episode list when playback is not a series episode.</summary>
    public void ClearSeries()
    {
        SelectedSeason = null;
        Seasons.Clear();
        Episodes.Clear();
        CanBrowseEpisodes = false;
    }

    private void BuildEpisodes(MetaItem series, PlaybackRequest request)
    {
        var entries = _watchProgress.GetEpisodeEntries(series.Id);

        var groups = series.Videos?
            .Where(video => video.EpisodeNumber > 0)
            .GroupBy(video => video.Season)
            .OrderBy(group => group.Key == 0 ? int.MaxValue : group.Key)
            .ThenBy(group => group.Key)
            .Select(group => new PlayerSeason(
                group.Key,
                group.OrderBy(video => video.EpisodeNumber)
                    .Select(video => new PlayerEpisode(
                        video,
                        entries.TryGetValue(
                            WatchProgressStore.EpisodeKey(video.Season, video.EpisodeNumber),
                            out var entry) ? entry : null))
                    .ToList()))
            .ToList() ?? [];

        SelectedSeason = null;
        Episodes.Clear();
        Seasons.Clear();

        foreach (var season in groups)
        {
            Seasons.Add(season);
        }

        CanBrowseEpisodes = Seasons.Count > 0;

        SelectedSeason = Seasons.FirstOrDefault(season => season.Number == request.Season)
            ?? Seasons.FirstOrDefault();

        MarkCurrentEpisode(request.Season, request.Episode);
    }

    private async Task FillAsync(
        StreamLoadSession session,
        TaskCompletionSource<bool> firstSource,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var subscription = session.Subscribe();

            await foreach (var result in subscription.WithCancellation(cancellationToken))
            {
                if (version != _loadVersion)
                {
                    return;
                }

                ApplyResult(result);

                if (Sources.Count > 0)
                {
                    firstSource.TrySetResult(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A new request replaced this one; its own fill loop owns the UI.
        }
        catch (Exception exception)
        {
            if (version == _loadVersion)
            {
                HasError = true;
                ErrorMessage = $"Couldn't find a source. {exception.Message}";
            }
        }
        finally
        {
            if (version != _loadVersion)
            {
                firstSource.TrySetResult(false);
            }
            else
            {
                IsLoading = false;

                if (Sources.Count == 0)
                {
                    var failure = _results
                        .Where(result => !string.IsNullOrWhiteSpace(result.Error))
                        .Select(result => $"{result.AddonName}: {result.Error}")
                        .FirstOrDefault();

                    HasError = true;
                    ErrorMessage = failure is null
                        ? "No playable sources were returned for this title."
                        : $"Couldn't get sources. {failure}";
                    StatusText = "No sources";
                }
                else
                {
                    HasError = false;
                    ErrorMessage = "";
                    StatusText = SourcesLabel;
                }

                RaiseSourceInfo();
                firstSource.TrySetResult(Sources.Count > 0);
            }
        }
    }

    /// <summary>
    /// Merges one addon's answer into the picker: duplicate URLs keep the
    /// highest-priority addon's entry, and groups follow the user's addon order.
    /// </summary>
    private void ApplyResult(AddonStreamResult result)
    {
        _results.Add(result);
        RebuildSources();
    }

    private void RebuildSources()
    {
        var existing = Sources.ToDictionary(
            source => source.Url,
            StringComparer.OrdinalIgnoreCase);
        var winners = new Dictionary<string, PlaybackSource>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in _results)
        {
            // A disabled or removed addon stops contributing immediately.
            if (_registry.GetEntry(result.ManifestUrl) is not { IsEnabled: true })
            {
                continue;
            }

            foreach (var stream in result.Streams)
            {
                var candidate = PlaybackSource.From(result.AddonName, result.ManifestUrl, stream);
                if (candidate is null)
                {
                    continue;
                }

                if (winners.TryGetValue(candidate.Url, out var current) &&
                    !IsBetter(candidate, current))
                {
                    continue;
                }

                if (existing.TryGetValue(candidate.Url, out var previous) &&
                    SameStream(previous, candidate))
                {
                    winners[candidate.Url] = previous;
                    continue;
                }

                // Keep the playing indicator when another addon takes the URL.
                if (existing.TryGetValue(candidate.Url, out var old))
                {
                    candidate.IsCurrent = old.IsCurrent;
                }

                winners[candidate.Url] = candidate;
            }
        }

        var ordered = winners.Values
            .OrderBy(source => _registry.GetRank(source.AddonUrl))
            .ThenByDescending(source => source.Score)
            .ThenBy(source => source.Size ?? long.MaxValue)
            .ToList();

        if (!Sources.SequenceEqual(ordered))
        {
            var selected = SelectedSource;
            Sources.Clear();
            foreach (var source in ordered)
            {
                Sources.Add(source);
            }

            // Nothing is playing yet on a fresh list; the picker stays on
            // "Choose a source" until the user (or autoplay) picks one.
            SelectedSource = selected is not null && ordered.Contains(selected)
                ? selected
                : null;

            RebuildSourceTabs(ordered);
        }

        RaiseSourceInfo();
    }

    /// <summary>
    /// Splits the ranked list into "All" plus one tab per addon, keeping the
    /// tab the user is looking at when a rebuild leaves it in place.
    /// </summary>
    private void RebuildSourceTabs(IReadOnlyList<PlaybackSource> ordered)
    {
        var selectedKey = SelectedSourceTab?.Key;

        var tabs = new List<SourceTab>
        {
            new() { Key = null, Title = "All", Sources = ordered },
        };

        tabs.AddRange(ordered
            .GroupBy(
                source => source.AddonUrl ?? source.AddonName,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new SourceTab
            {
                Key = group.Key,
                Title = TabTitleFor(group.First().AddonName),
                Sources = group.ToList(),
            }));

        _sourceTabs = tabs;
        OnPropertyChanged(nameof(SourceTabs));
        OnPropertyChanged(nameof(HasMultipleSourceTabs));

        SelectedSourceTab = tabs.FirstOrDefault(tab =>
            string.Equals(tab.Key, selectedKey, StringComparison.OrdinalIgnoreCase)) ?? tabs[0];
    }

    private void ClearSourceTabs()
    {
        _sourceTabs = [];
        SelectedSourceTab = null;
        OnPropertyChanged(nameof(SourceTabs));
        OnPropertyChanged(nameof(HasMultipleSourceTabs));
    }

    private void RefreshVisibleSources()
    {
        VisibleSources.Clear();

        if (SelectedSourceTab is { } tab)
        {
            foreach (var source in tab.Sources)
            {
                VisibleSources.Add(source);
            }
        }

        OnPropertyChanged(nameof(SourceFlyoutDetail));
    }

    /// <summary>Long addon names would make the tab bar scroll for no reason.</summary>
    private static string TabTitleFor(string addonName)
    {
        var name = addonName.Trim();
        return name.Length > MaxSourceTabTitleLength
            ? string.Concat(name.AsSpan(0, MaxSourceTabTitleLength - 1), "…")
            : name;
    }

    private bool IsBetter(PlaybackSource candidate, PlaybackSource existing)
    {
        var candidateRank = _registry.GetRank(candidate.AddonUrl);
        var existingRank = _registry.GetRank(existing.AddonUrl);

        if (candidateRank != existingRank)
        {
            return candidateRank < existingRank;
        }

        if (candidate.Score != existing.Score)
        {
            return candidate.Score > existing.Score;
        }

        return (candidate.Size ?? long.MaxValue) < (existing.Size ?? long.MaxValue);
    }

    private static bool SameStream(PlaybackSource left, PlaybackSource right) =>
        string.Equals(left.AddonUrl, right.AddonUrl, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.AddonName, right.AddonName, StringComparison.Ordinal) &&
        left.Score == right.Score &&
        left.QualityLabel == right.QualityLabel;

    /// <summary>
    /// Queries every configured addon for subtitle tracks and groups them by
    /// language while keeping every version. The picker shows languages first,
    /// then the individual release/file versions for the chosen language.
    /// The preferred language is pinned above the rest; every other language
    /// stays available alphabetically, because the picker is where a title
    /// gets overridden, not where the preference is set.
    /// </summary>
    public async Task LoadSubtitlesAsync(
        PlaybackRequest request,
        CancellationToken cancellationToken = default,
        string? preferredLanguage = null)
    {
        IsLoadingSubtitles = true;
        SubtitleStatus = "Loading subtitles…";
        OnPropertyChanged(nameof(HasSubtitles));

        try
        {
            await _registry.EnsureLoadedAsync(cancellationToken);

            var found = new List<(SubtitleItem Item, string AddonName)>();

            foreach (var entry in _registry.EnabledEntries)
            {
                try
                {
                    var tracks = await entry.Client.GetSubtitlesAsync(
                        request.Type,
                        request.VideoId,
                        cancellationToken);

                    foreach (var track in tracks.Where(track => track.HasUrl))
                    {
                        found.Add((track, entry.Name));
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // One subtitle addon failing must not hide the others.
                }
            }

            var preferredCode = SubtitleLanguages.CanonicalCode(preferredLanguage);
            var languages = found
                .GroupBy(item => NormalizeLanguage(item.Item.Lang))
                .Select(group =>
                {
                    var name = SubtitleLanguages.DisplayName(group.Key);
                    var versions = group
                        .Select((entry, index) => SubtitleVersionChoice.Create(
                            name,
                            group.Key,
                            entry.Item,
                            entry.AddonName,
                            index + 1))
                        .ToList();

                    var detail = versions.Count == 1
                        ? $"1 version · {versions[0].AddonName}"
                        : $"{versions.Count} versions · {versions[0].AddonName}";

                    var isPreferred = preferredCode.Length > 0 &&
                                      SubtitleLanguages.SameLanguage(group.Key, preferredCode);

                    return new SubtitleLanguageChoice(
                        name,
                        group.Key,
                        isOff: false,
                        versions,
                        isPreferred ? $"Preferred · {detail}" : detail,
                        isPreferred: isPreferred);
                })
                .OrderByDescending(language => language.IsPreferred)
                .ThenBy(language => language.LanguageName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            // Embedded tracks belong to the stream, not to the addon query, so
            // they survive the language list being rebuilt.
            var embedded = SubtitleMenu.Where(language => language.IsEmbedded).ToList();
            var pinned = languages.FirstOrDefault(language => language.IsPreferred);

            SubtitleMenu.Clear();
            SubtitleMenu.Add(OffSubtitle);

            if (pinned is not null)
            {
                SubtitleMenu.Add(pinned);
            }

            foreach (var language in embedded)
            {
                SubtitleMenu.Add(language);
            }

            foreach (var language in languages)
            {
                if (!ReferenceEquals(language, pinned))
                {
                    SubtitleMenu.Add(language);
                }
            }

            SelectedSubtitle = null;
            OnPropertyChanged(nameof(HasSubtitles));
            SubtitleStatus = HasSubtitles ? "" : "No subtitles found";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            SubtitleStatus = "Couldn't load subtitles";
        }
        finally
        {
            IsLoadingSubtitles = false;
        }
    }

    public void SelectSubtitle(SubtitleVersionChoice? version)
    {
        SelectedSubtitle = version;
    }

    /// <summary>
    /// Replaces the subtitle tracks embedded in the current stream with the
    /// ones the playback engine just parsed. They sit under Off and under the
    /// preferred language, before the rest of the addon languages, because they
    /// need no download and are usually the best match for the picture.
    /// </summary>
    public void SetEmbeddedSubtitles(IReadOnlyList<SubtitleLanguageChoice> tracks)
    {
        for (var i = SubtitleMenu.Count - 1; i >= 0; i--)
        {
            if (SubtitleMenu[i].IsEmbedded)
            {
                SubtitleMenu.RemoveAt(i);
            }
        }

        var index = SubtitleMenu.Count > 1 && SubtitleMenu[1].IsPreferred ? 2 : 1;
        foreach (var track in tracks)
        {
            SubtitleMenu.Insert(index++, track);
        }

        // "No subtitles found" only refers to the addon query, and an embedded
        // track makes the panel useful again.
        if (tracks.Count > 0 && SubtitleStatus == "No subtitles found")
        {
            SubtitleStatus = "";
        }

        OnPropertyChanged(nameof(HasSubtitles));
    }

    /// <summary>
    /// The best track for a preferred language, when this title has one. Used
    /// to turn subtitles on automatically for the configured language.
    /// </summary>
    public SubtitleVersionChoice? FindSubtitleForLanguage(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        return SubtitleMenu
            .FirstOrDefault(language =>
                !language.IsOff &&
                SubtitleLanguages.SameLanguage(language.LanguageCode, languageCode))
            ?.Versions.FirstOrDefault();
    }

    private static string NormalizeLanguage(string? lang) =>
        string.IsNullOrWhiteSpace(lang) ? "unknown" : lang.Trim().ToLowerInvariant();

    private void RaiseSourceInfo()
    {
        OnPropertyChanged(nameof(SourceCount));
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(SourcesLabel));
        OnPropertyChanged(nameof(SourceFlyoutDetail));
    }

    partial void OnRequestChanged(PlaybackRequest? value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(BackdropImage));
        OnPropertyChanged(nameof(PosterImage));
        OnPropertyChanged(nameof(LoadingArtworkImage));
        OnPropertyChanged(nameof(LogoImage));
        OnPropertyChanged(nameof(HasLogo));
        OnPropertyChanged(nameof(NoLogo));
    }

    partial void OnSelectedSeasonChanged(PlayerSeason? value)
    {
        Episodes.Clear();

        if (value is not null)
        {
            foreach (var episode in value.Episodes)
            {
                Episodes.Add(episode);
            }
        }
    }
}

/// <summary>
/// One row in the first subtitle step: Off or a language with all of its
/// available versions.
/// </summary>
public sealed class SubtitleLanguageChoice : ObservableObject
{
    public SubtitleLanguageChoice(
        string languageName,
        string languageCode,
        bool isOff,
        IReadOnlyList<SubtitleVersionChoice> versions,
        string detail,
        long embeddedTrackId = -1,
        bool isPreferred = false)
    {
        LanguageName = languageName;
        LanguageCode = languageCode;
        IsOff = isOff;
        Versions = versions;
        Detail = detail;
        EmbeddedTrackId = embeddedTrackId;
        IsPreferred = isPreferred;
    }

    public string LanguageName { get; }

    public string LanguageCode { get; }

    public bool IsOff { get; }

    /// <summary>
    /// The language configured in Settings → Playback. It is pinned at the top
    /// of the picker and labelled in <see cref="Detail"/>.
    /// </summary>
    public bool IsPreferred { get; }

    /// <summary>Playback engine track id when this row is embedded in the video itself.</summary>
    public long EmbeddedTrackId { get; }

    public bool IsEmbedded => EmbeddedTrackId >= 0;

    public IReadOnlyList<SubtitleVersionChoice> Versions { get; }

    public string Detail { get; }

    public int Count => Versions.Count;

    public bool HasVersions => Versions.Count > 1;

    public override string ToString() => LanguageName;
}

/// <summary>
/// One selectable subtitle file for a language. Release group, file name,
/// frame rate and encoding help identify the version that matches the video.
/// </summary>
public sealed class SubtitleVersionChoice : ObservableObject
{
    public required string LanguageName { get; init; }

    public required string LanguageCode { get; init; }

    public required string Url { get; init; }

    public required string AddonName { get; init; }

    public required string Label { get; init; }

    public required string Detail { get; init; }

    public string? FileName { get; init; }

    public string? ReleaseGroup { get; init; }

    public double? Fps { get; init; }

    public override string ToString() => Label;

    public static SubtitleVersionChoice Create(
        string languageName,
        string languageCode,
        SubtitleItem item,
        string addonName,
        int ordinal)
    {
        var fileName = string.IsNullOrWhiteSpace(item.FileName)
            ? null
            : Path.GetFileName(item.FileName);
        var fileBase = fileName is null ? null : Path.GetFileNameWithoutExtension(fileName);
        var group = string.IsNullOrWhiteSpace(item.ReleaseGroup) ? null : item.ReleaseGroup.Trim();

        var label = group
            ?? (string.IsNullOrWhiteSpace(item.Name) ? null : item.Name.Trim())
            ?? fileBase
            ?? $"Version {ordinal}";

        var detail = new List<string>();
        if (fileName is not null &&
            !fileName.Equals(label, StringComparison.OrdinalIgnoreCase))
        {
            detail.Add(fileName);
        }

        detail.Add(addonName);

        if (item.FpsMilli is > 0)
        {
            detail.Add($"{item.FpsMilli.Value / 1000.0:0.###} fps");
        }

        if (!string.IsNullOrWhiteSpace(item.Encoding))
        {
            detail.Add(item.Encoding.Trim());
        }

        return new SubtitleVersionChoice
        {
            LanguageName = languageName,
            LanguageCode = languageCode,
            Url = item.Url!,
            AddonName = addonName,
            Label = label,
            Detail = string.Join(" · ", detail),
            FileName = fileName,
            ReleaseGroup = group,
            Fps = item.FpsMilli is > 0 ? item.FpsMilli.Value / 1000.0 : null,
        };
    }
}

/// <summary>
/// One tab in the source picker. A null <see cref="Key"/> is the "All" tab,
/// otherwise the key is the manifest URL (or name) of the addon.
/// </summary>
public sealed class SourceTab
{
    public string? Key { get; init; }

    public required string Title { get; init; }

    public required IReadOnlyList<PlaybackSource> Sources { get; init; }

    public int Count => Sources.Count;

    public override string ToString() => Title;
}

/// <summary>A season group in the in-player episode list.</summary>
public sealed class PlayerSeason(int number, IReadOnlyList<PlayerEpisode> episodes)
{
    public int Number { get; } = number;

    public string Label { get; } = number == 0 ? "Specials" : $"Season {number}";

    public IReadOnlyList<PlayerEpisode> Episodes { get; } = episodes;

    public string DisplayLabel => Label;

    public override string ToString() => Label;
}

/// <summary>
/// One episode row in the player: the addon's episode plus the local watch
/// state and the "now playing" marker.
/// </summary>
public sealed class PlayerEpisode : ObservableObject
{
    private bool _isCurrent;

    public PlayerEpisode(MetaVideo video, WatchProgressEntry? entry)
    {
        Video = video;
        IsWatched = entry?.IsWatched == true;
        WatchProgress = entry is null
            ? 0
            : entry.Completed
                ? 1
                : entry.ProgressFraction;
    }

    public MetaVideo Video { get; }

    public int Season => Video.Season;

    public int EpisodeNumber => Video.EpisodeNumber;

    public string EpisodeTag => Season > 0
        ? $"S{Season:00}E{EpisodeNumber:00}"
        : $"E{EpisodeNumber:00}";

    public string Title => Video.Title;

    public string DescriptionText => Video.DescriptionText;

    public BitmapImage? ThumbnailImage => Video.ThumbnailImage;

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    public bool IsWatched { get; }

    public double WatchProgress { get; }

    public bool HasWatchProgress => WatchProgress is > 0 and < 1 && !IsWatched;

    public override string ToString() => $"{EpisodeTag} {Title}";
}

/// <summary>
/// A source the user can play, with normalized quality/provider metadata.
/// </summary>
public sealed class PlaybackSource : ObservableObject
{
    private static readonly Regex HeightPattern = new(
        @"(?<!\d)(?<height>\d{3,4})p\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public required string Url { get; init; }

    public required string AddonName { get; init; }

    /// <summary>Manifest URL of the addon that returned this source.</summary>
    public string? AddonUrl { get; init; }

    private bool _isCurrent;

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    public string QualityLabel { get; init; } = "Auto";

    public string ProviderLine { get; init; } = "";

    public string DetailsLine { get; init; } = "";

    public string SizeLabel { get; init; } = "";

    public string FormatLabel { get; init; } = "";

    public long? Size { get; init; }

    public bool IsHls { get; init; }

    public int Score { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public string Title => string.IsNullOrWhiteSpace(ProviderLine)
        ? $"{QualityLabel} · {AddonName}"
        : $"{QualityLabel} · {ProviderLine}";

    public string SubtitleLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(SizeLabel)) parts.Add(SizeLabel);
            if (!string.IsNullOrWhiteSpace(FormatLabel)) parts.Add(FormatLabel);
            if (!string.IsNullOrWhiteSpace(AddonName)) parts.Add(AddonName);
            return string.Join(" · ", parts);
        }
    }

    public bool HasDetails => !string.IsNullOrWhiteSpace(DetailsLine);

    /// <summary>
    /// Picker row headline. The quality has its own badge, so the headline is
    /// the release/provider the addon reported, or just the addon name.
    /// </summary>
    public string PrimaryLine => string.IsNullOrWhiteSpace(ProviderLine) ? AddonName : ProviderLine;

    public static PlaybackSource? From(string addonName, string? addonUrl, StreamItem stream)
    {
        if (!stream.HasDirectUrl || stream.Url is null ||
            !Uri.TryCreate(stream.Url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var haystack = $"{stream.Name} {stream.Description} {stream.BehaviorHints?.Filename}";
        var height = ParseHeight(haystack);

        // Some addons omit resolution text but include it in the URL path.
        if (height == 0)
        {
            height = ParseHeight(stream.Url ?? "");
        }

        var quality = height switch
        {
            >= 2000 => "4K",
            > 0 => $"{height}p",
            _ => "Unknown",
        };

        var extension = Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToUpperInvariant();
        var isHls = extension.Equals("M3U8", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals("M3U", StringComparison.OrdinalIgnoreCase);

        var score = height switch
        {
            1080 => 500,
            720 => 400,
            2160 => 360,
            480 => 200,
            360 => 150,
            _ => 100,
        };

        if (isHls) score += 40;

        var lines = (stream.Description ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Trim().TrimStart('»').Trim())
            .Where(line => line.Length > 0)
            .ToList();

        var provider = lines
            .Skip(1)
            .Select(line => line.Split('|').Last().Trim())
            .FirstOrDefault(line => line.Length > 0)
            ?? addonName;

        if (provider.Length > 70)
        {
            provider = provider[..67] + "…";
        }

        var details = string.Join(" · ", lines.Skip(2).Take(2));

        return new PlaybackSource
        {
            Url = stream.Url!,
            AddonName = addonName,
            AddonUrl = addonUrl,
            QualityLabel = quality,
            ProviderLine = provider,
            DetailsLine = details,
            SizeLabel = FormatSize(stream.BehaviorHints?.VideoSize),
            FormatLabel = extension switch
            {
                "M3U8" or "M3U" => "HLS",
                "MPD" => "DASH",
                "MP4" => "MP4",
                "MKV" => "MKV",
                _ => "Stream",
            },
            Size = stream.BehaviorHints?.VideoSize,
            IsHls = isHls,
            Score = score,
            Headers = stream.BehaviorHints?.ProxyHeaders?.Request,
        };
    }

    private static int ParseHeight(string text)
    {
        if (Regex.IsMatch(text, @"\b(4k|uhd|2160p)\b", RegexOptions.IgnoreCase))
        {
            return 2160;
        }

        var match = HeightPattern.Match(text);
        return match.Success && int.TryParse(match.Groups["height"].Value, out var height)
            ? height
            : 0;
    }

    public override string ToString() => Title;

    private static string FormatSize(long? bytes)
    {
        if (bytes is not > 0)
        {
            return "";
        }

        const double gb = 1024d * 1024d * 1024d;
        var value = bytes.Value / gb;
        return value >= 1
            ? $"{value:0.#} GB"
            : $"{bytes.Value / (1024d * 1024d):0} MB";
    }
}

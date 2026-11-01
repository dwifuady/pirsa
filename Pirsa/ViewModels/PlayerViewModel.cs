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

    public PlayerViewModel(AddonRegistry registry)
    {
        _registry = registry;
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
    }

    public ObservableCollection<PlaybackSource> Sources { get; } = [];

    /// <summary>Flat picker rows: addon group headers interleaved with sources.</summary>
    public ObservableCollection<SourceListItem> SourceItems { get; } = [];

    /// <summary>Off + one entry per available language.</summary>
    public ObservableCollection<SubtitleLanguageChoice> SubtitleMenu { get; } = [];

    public SubtitleLanguageChoice OffSubtitle { get; }

    [ObservableProperty]
    public partial PlaybackRequest? Request { get; set; }

    [ObservableProperty]
    public partial PlaybackSource? SelectedSource { get; set; }

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

    public bool HasSubtitles => SubtitleMenu.Count > 1;

    public string Title => Request?.Title ?? "";

    public string Subtitle => Request?.Subtitle ?? "";

    public BitmapImage? BackdropImage => Request?.Meta.BackgroundImage;

    public BitmapImage? PosterImage => Request?.Meta.PosterDetailImage;

    public int SourceCount => Sources.Count;

    public bool HasSources => Sources.Count > 0;

    public string SourcesLabel => SourceCount switch
    {
        0 => "Sources",
        1 => "1 source",
        _ => $"{SourceCount} sources",
    };

    /// <summary>
    /// Loads streams from every configured addon in parallel, deduplicates by
    /// URL, and ranks the results for a sensible default (1080p HLS first).
    /// </summary>
    public async Task<bool> LoadAsync(PlaybackRequest request, CancellationToken cancellationToken = default)
    {
        Request = request;
        SelectedSource = null;
        Sources.Clear();
        SourceItems.Clear();
        HasError = false;
        ErrorMessage = "";
        StatusText = "Finding sources…";
        IsLoading = true;
        RaiseSourceInfo();

        try
        {
            await _registry.EnsureLoadedAsync(cancellationToken);

            var addons = _registry.Entries
                .Select(entry => entry.Client)
                .ToList();

            if (addons.Count == 0)
            {
                HasError = true;
                ErrorMessage = "No addons are configured. Add a stream addon in Settings → Addons.";
                StatusText = "No sources";
                return false;
            }

            var results = await Task.WhenAll(
                addons.Select(addon => FetchSourcesAsync(addon, request, cancellationToken)));

            var ranked = results
                .SelectMany(result => result.Sources)
                .GroupBy(source => source.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(source => source.Score)
                    .ThenBy(source => source.Size ?? long.MaxValue)
                    .First())
                .OrderByDescending(source => source.Score)
                .ThenBy(source => source.Size ?? long.MaxValue)
                .ToList();

            foreach (var source in ranked)
            {
                Sources.Add(source);
            }

            foreach (var group in ranked.GroupBy(source => source.AddonName))
            {
                SourceItems.Add(new SourceListItem
                {
                    IsHeader = true,
                    HeaderTitle = group.Key,
                    HeaderDetail = group.Count() == 1 ? "1 source" : $"{group.Count()} sources",
                });

                foreach (var source in group)
                {
                    SourceItems.Add(new SourceListItem { Source = source });
                }
            }

            if (Sources.Count == 0)
            {
                var failure = results
                    .Where(result => !string.IsNullOrWhiteSpace(result.Error))
                    .Select(result => $"{result.AddonName}: {result.Error}")
                    .FirstOrDefault();

                HasError = true;
                ErrorMessage = failure is null
                    ? "No playable sources were returned for this title."
                    : $"Couldn't get sources. {failure}";
                StatusText = "No sources";
                return false;
            }

            SelectedSource = Sources[0];
            StatusText = SourcesLabel;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            HasError = true;
            ErrorMessage = $"Couldn't find a source. {exception.Message}";
            StatusText = "No sources";
            return false;
        }
        finally
        {
            IsLoading = false;
            RaiseSourceInfo();
        }
    }

    /// <summary>
    /// Queries every configured addon for subtitle tracks and groups them by
    /// language while keeping every version. The picker shows languages first,
    /// then the individual release/file versions for the chosen language.
    /// </summary>
    public async Task LoadSubtitlesAsync(
        PlaybackRequest request,
        CancellationToken cancellationToken = default)
    {
        IsLoadingSubtitles = true;
        SubtitleStatus = "Loading subtitles…";
        OnPropertyChanged(nameof(HasSubtitles));

        try
        {
            await _registry.EnsureLoadedAsync(cancellationToken);

            var found = new List<(SubtitleItem Item, string AddonName)>();

            foreach (var entry in _registry.Entries)
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

                    return new SubtitleLanguageChoice(
                        name,
                        group.Key,
                        isOff: false,
                        versions,
                        detail);
                })
                .OrderByDescending(language => language.Count)
                .ThenBy(language => language.LanguageName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            SubtitleMenu.Clear();
            SubtitleMenu.Add(OffSubtitle);
            foreach (var language in languages)
            {
                SubtitleMenu.Add(language);
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

    private static string NormalizeLanguage(string? lang) =>
        string.IsNullOrWhiteSpace(lang) ? "unknown" : lang.Trim().ToLowerInvariant();

    private static async Task<AddonFetchResult> FetchSourcesAsync(
        StremioAddonClient addon,
        PlaybackRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var streams = await addon.GetStreamsAsync(request.Type, request.VideoId, cancellationToken);
            var sources = streams
                .Select(stream => PlaybackSource.From(addon.AddonName, stream))
                .OfType<PlaybackSource>()
                .ToList();

            return new AddonFetchResult(addon.AddonName, null, sources);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new AddonFetchResult(addon.AddonName, exception.Message, []);
        }
    }

    private void RaiseSourceInfo()
    {
        OnPropertyChanged(nameof(SourceCount));
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(SourcesLabel));
    }

    partial void OnRequestChanged(PlaybackRequest? value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(BackdropImage));
        OnPropertyChanged(nameof(PosterImage));
    }

    private sealed record AddonFetchResult(
        string AddonName,
        string? Error,
        List<PlaybackSource> Sources);
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
        string detail)
    {
        LanguageName = languageName;
        LanguageCode = languageCode;
        IsOff = isOff;
        Versions = versions;
        Detail = detail;
    }

    public string LanguageName { get; }

    public string LanguageCode { get; }

    public bool IsOff { get; }

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

/// <summary>A picker row: either an addon group header or one playable source.</summary>
public sealed class SourceListItem : ObservableObject
{
    public bool IsHeader { get; init; }

    public string HeaderTitle { get; init; } = "";

    public string HeaderDetail { get; init; } = "";

    public PlaybackSource? Source { get; init; }

    public override string ToString() =>
        IsHeader ? HeaderTitle : Source?.Title ?? "";
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

    public static PlaybackSource? From(string addonName, StreamItem stream)
    {
        if (!stream.HasDirectUrl || stream.Url is null ||
            !Uri.TryCreate(stream.Url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var haystack = $"{stream.Name} {stream.Description} {stream.BehaviorHints?.Filename}";
        var height = ParseHeight(haystack);
        var quality = height switch
        {
            >= 2000 => "4K",
            > 0 => $"{height}p",
            _ => "Auto",
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
            Url = stream.Url,
            AddonName = addonName,
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

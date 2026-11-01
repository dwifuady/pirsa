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

        OffSubtitle = new SubtitleChoice(
            "Off",
            "No subtitles",
            null,
            "",
            isOff: true);
        Subtitles.Add(OffSubtitle);
    }

    public ObservableCollection<PlaybackSource> Sources { get; } = [];

    public ObservableCollection<SubtitleChoice> Subtitles { get; } = [];

    public SubtitleChoice OffSubtitle { get; }

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
    public partial SubtitleChoice? SelectedSubtitle { get; set; }

    public bool HasSubtitles => Subtitles.Count > 1;

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
    /// Queries every configured addon for subtitle tracks and groups the
    /// results by language. One representative track per language keeps the
    /// picker short; Off is always the first row.
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
                    var first = group.First();
                    var count = group.Count();
                    var name = SubtitleLanguages.DisplayName(group.Key);
                    var detail = count > 1
                        ? $"{count} versions · {first.AddonName}"
                        : first.AddonName;

                    return new SubtitleChoice(
                        name,
                        detail,
                        first.Item.Url,
                        group.Key,
                        count: count);
                })
                .OrderByDescending(choice => choice.Count)
                .ThenBy(choice => choice.LanguageName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            Subtitles.Clear();
            Subtitles.Add(OffSubtitle);
            foreach (var choice in languages)
            {
                Subtitles.Add(choice);
            }

                SelectedSubtitle = OffSubtitle;
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

    public void SelectSubtitle(SubtitleChoice choice)
    {
        SelectedSubtitle = choice;
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
/// One row in the subtitle picker: either the Off choice or the best track for
/// a language. <see cref="IsSelected"/> drives the check mark in the list.
/// </summary>
public sealed partial class SubtitleChoice : ObservableObject
{
    public SubtitleChoice(
        string languageName,
        string detail,
        string? url,
        string languageCode,
        bool isOff = false,
        int count = 0)
    {
        LanguageName = languageName;
        Detail = detail;
        Url = url;
        LanguageCode = languageCode;
        IsOff = isOff;
        Count = count;
    }

    public string LanguageName { get; }

    public string Detail { get; }

    public string? Url { get; }

    public string LanguageCode { get; }

    public bool IsOff { get; }

    public int Count { get; }

    public override string ToString() => LanguageName;
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

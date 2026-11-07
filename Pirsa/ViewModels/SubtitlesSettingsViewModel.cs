using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pirsa.Helpers;
using Pirsa.Services;

namespace Pirsa.ViewModels;

/// <summary>
/// Settings → Subtitles. The preferred language turns the first matching track
/// on automatically; per-title picks in the player still win for that title.
/// </summary>
public partial class SubtitlesSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SubtitleOffsetStore _offsets;
    private readonly SubtitleChoiceStore _choices;
    private bool _loadingSettings;

    public SubtitlesSettingsViewModel(
        AppSettings settings,
        SubtitleOffsetStore offsets,
        SubtitleChoiceStore choices)
    {
        _settings = settings;
        _offsets = offsets;
        _choices = choices;

        // Off first, then every language the subtitle picker understands.
        SubtitleLanguageOptions.Add(new SubtitleLanguageOption("", "Off"));
        foreach (var option in SubtitleLanguages.PreferenceOptions)
        {
            SubtitleLanguageOptions.Add(option);
        }

        _loadingSettings = true;
        SelectedSubtitleLanguage = SubtitleLanguageOptions[0];
        SubtitleTextSizeIndex = 1;
        SubtitleBackgroundIndex = 1;
        _loadingSettings = false;
    }

    /// <summary>Off plus every language subtitles can turn on automatically.</summary>
    public ObservableCollection<SubtitleLanguageOption> SubtitleLanguageOptions { get; } = [];

    [ObservableProperty]
    public partial SubtitleLanguageOption? SelectedSubtitleLanguage { get; set; }

    /// <summary>0 = Small, 1 = Default, 2 = Large, 3 = Extra large.</summary>
    [ObservableProperty]
    public partial int SubtitleTextSizeIndex { get; set; }

    /// <summary>0 = None, 1 = Dim, 2 = Solid.</summary>
    [ObservableProperty]
    public partial int SubtitleBackgroundIndex { get; set; }

    public double PreviewFontSize => SubtitleTextSizeIndex switch
    {
        0 => 13,
        2 => 20,
        3 => 24,
        _ => 16,
    };

    [ObservableProperty]
    public partial bool HasNotice { get; set; }

    [ObservableProperty]
    public partial string NoticeMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = "";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _settings.EnsureLoadedAsync(cancellationToken);

        _loadingSettings = true;
        SelectedSubtitleLanguage = ResolveSubtitleLanguage(_settings.PreferredSubtitleLanguage);
        SubtitleTextSizeIndex = _settings.SubtitleTextScale switch
        {
            < 0.92 => 0,
            >= 1.35 => 3,
            >= 1.1 => 2,
            _ => 1,
        };
        SubtitleBackgroundIndex = _settings.SubtitleBackground switch
        {
            "None" => 0,
            "Solid" => 2,
            _ => 1,
        };
        _loadingSettings = false;
    }

    public async Task ClearSavedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _offsets.ClearAsync(cancellationToken);
            await _choices.ClearAsync(cancellationToken);
            HasError = false;
            ErrorMessage = "";
            NoticeMessage = "Saved subtitle timing and per-title choices were cleared.";
            HasNotice = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Couldn't clear subtitle timing. {exception.Message}";
            HasError = true;
            HasNotice = false;
            NoticeMessage = "";
        }
    }

    public void DismissNotice() => HasNotice = false;

    partial void OnSelectedSubtitleLanguageChanged(SubtitleLanguageOption? value)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.PreferredSubtitleLanguage = value?.Code ?? "";
        _ = _settings.SaveAsync();

        // Changing the default resets per-title picks, so a title can never
        // keep a language the viewer no longer expects.
        _ = _choices.ClearAsync();
    }

    partial void OnSubtitleTextSizeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(PreviewFontSize));

        if (!_loadingSettings)
        {
            _settings.SubtitleTextScale = value switch
            {
                0 => 0.85,
                2 => 1.2,
                3 => 1.45,
                _ => 1.0,
            };
            _ = _settings.SaveAsync();
        }
    }

    partial void OnSubtitleBackgroundIndexChanged(int value)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.SubtitleBackground = value switch
        {
            0 => "None",
            2 => "Solid",
            _ => "Dim",
        };
        _ = _settings.SaveAsync();
    }

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
}

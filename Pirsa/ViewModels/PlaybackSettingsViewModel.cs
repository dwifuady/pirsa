using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pirsa.Helpers;
using Pirsa.Playback;
using Pirsa.Services;

namespace Pirsa.ViewModels;

/// <summary>
/// Settings → Playback. Autoplay, quality, episode chaining and the video
/// engine. Every change is saved immediately; there is no Save button.
/// </summary>
public partial class PlaybackSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private bool _loadingSettings;

    public PlaybackSettingsViewModel(AppSettings settings)
    {
        _settings = settings;

        // Automatic first, then every backend this build can actually start.
        PlaybackEngineOptions.Add(new PlaybackEngineOption("", "Automatic"));
        foreach (var factory in PlaybackEngines.All.Where(factory => factory.IsAvailable))
        {
            PlaybackEngineOptions.Add(new PlaybackEngineOption(factory.Id, factory.DisplayName));
        }

        AudioLanguageOptions.Add(new AudioLanguageOption("", "Original"));
        foreach (var option in SubtitleLanguages.PreferenceOptions)
        {
            AudioLanguageOptions.Add(new AudioLanguageOption(option.Code, option.Name));
        }

        _loadingSettings = true;
        AutoplayEnabled = false;
        QualityIndex = 0;
        AutoAdvanceEnabled = true;
        AutoAdvanceDelayIndex = 0;
        ResumeEnabled = true;
        SelectedPlaybackEngine = PlaybackEngineOptions[0];
        SelectedAudioLanguage = AudioLanguageOptions[0];
        _loadingSettings = false;
    }

    /// <summary>0 = best, 1 = 4K, 2 = 1440p, 3 = 1080p, 4 = 720p, 5 = 480p.</summary>
    public static readonly string[] QualityCodes = ["", "4K", "1440p", "1080p", "720p", "480p"];

    /// <summary>0 = immediately, then 5, 10, 15 and 30 seconds.</summary>
    public static readonly int[] AutoAdvanceDelayCodes = [0, 5, 10, 15, 30];

    [ObservableProperty]
    public partial bool AutoplayEnabled { get; set; }

    /// <summary>When true, the next episode starts when the current one ends.</summary>
    [ObservableProperty]
    public partial bool AutoAdvanceEnabled { get; set; }

    /// <summary>0 = immediately, 1 = 5s, 2 = 10s, 3 = 15s, 4 = 30s.</summary>
    [ObservableProperty]
    public partial int AutoAdvanceDelayIndex { get; set; }

    /// <summary>When false, playback and details always start at 0.</summary>
    [ObservableProperty]
    public partial bool ResumeEnabled { get; set; }

    [ObservableProperty]
    public partial int QualityIndex { get; set; }

    /// <summary>Automatic plus every available playback backend.</summary>
    public ObservableCollection<PlaybackEngineOption> PlaybackEngineOptions { get; } = [];

    [ObservableProperty]
    public partial PlaybackEngineOption? SelectedPlaybackEngine { get; set; }

    /// <summary>Original plus every language the audio picker understands.</summary>
    public ObservableCollection<AudioLanguageOption> AudioLanguageOptions { get; } = [];

    [ObservableProperty]
    public partial AudioLanguageOption? SelectedAudioLanguage { get; set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _settings.EnsureLoadedAsync(cancellationToken);

        _loadingSettings = true;
        AutoplayEnabled = _settings.Autoplay;
        AutoAdvanceEnabled = _settings.AutoAdvance;
        var delayIndex = Array.IndexOf(AutoAdvanceDelayCodes, _settings.AutoAdvanceDelaySeconds);
        AutoAdvanceDelayIndex = delayIndex < 0 ? 0 : delayIndex;
        ResumeEnabled = _settings.ResumePlayback;
        SelectedPlaybackEngine = ResolvePlaybackEngine(_settings.PlaybackEngine);
        SelectedAudioLanguage = ResolveAudioLanguage(_settings.PreferredAudioLanguage);
        var qualityIndex = Array.IndexOf(QualityCodes, _settings.PreferredQuality ?? "");
        QualityIndex = qualityIndex < 0 ? 0 : qualityIndex;
        _loadingSettings = false;
    }

    partial void OnAutoplayEnabledChanged(bool value)
    {
        if (!_loadingSettings)
        {
            _settings.Autoplay = value;
            _ = _settings.SaveAsync();
        }
    }

    partial void OnAutoAdvanceEnabledChanged(bool value)
    {
        if (!_loadingSettings)
        {
            _settings.AutoAdvance = value;
            _ = _settings.SaveAsync();
        }
    }

    partial void OnAutoAdvanceDelayIndexChanged(int value)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.AutoAdvanceDelaySeconds =
            value >= 0 && value < AutoAdvanceDelayCodes.Length
                ? AutoAdvanceDelayCodes[value]
                : 0;
        _ = _settings.SaveAsync();
    }

    partial void OnResumeEnabledChanged(bool value)
    {
        if (!_loadingSettings)
        {
            _settings.ResumePlayback = value;
            _ = _settings.SaveAsync();
        }
    }

    partial void OnSelectedAudioLanguageChanged(AudioLanguageOption? value)
    {
        if (!_loadingSettings)
        {
            _settings.PreferredAudioLanguage = value?.Code ?? "";
            _ = _settings.SaveAsync();
        }
    }

    partial void OnQualityIndexChanged(int value)
    {
        if (!_loadingSettings)
        {
            _settings.PreferredQuality = value >= 0 && value < QualityCodes.Length
                ? QualityCodes[value]
                : "";
            _ = _settings.SaveAsync();
        }
    }

    partial void OnSelectedPlaybackEngineChanged(PlaybackEngineOption? value)
    {
        if (!_loadingSettings)
        {
            _settings.PlaybackEngine = value?.Id ?? "";
            _ = _settings.SaveAsync();
        }
    }

    /// <summary>
    /// Finds the audio picker row for a stored language. A code no release has
    /// offered yet is added so the preference is never silently lost.
    /// </summary>
    private AudioLanguageOption ResolveAudioLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return AudioLanguageOptions[0];
        }

        if (AudioLanguageOptions.FirstOrDefault(option =>
                !string.IsNullOrWhiteSpace(option.Code) &&
                SubtitleLanguages.SameLanguage(option.Code, code)) is { } match)
        {
            return match;
        }

        var extra = new AudioLanguageOption(
            SubtitleLanguages.CanonicalCode(code),
            SubtitleLanguages.DisplayName(code));
        AudioLanguageOptions.Add(extra);
        return extra;
    }

    /// <summary>
    /// Finds the picker row for a stored backend id. A choice this build no
    /// longer ships falls back to Automatic rather than failing to start.
    /// </summary>
    private PlaybackEngineOption ResolvePlaybackEngine(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return PlaybackEngineOptions[0];
        }

        return PlaybackEngineOptions.FirstOrDefault(option =>
                   option.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
               ?? PlaybackEngineOptions[0];
    }
}

/// <summary>One row in the video engine picker. An empty id means Automatic.</summary>
public sealed record PlaybackEngineOption(string Id, string Name);

/// <summary>One row in the audio picker. An empty code means Original.</summary>
public sealed record AudioLanguageOption(string Code, string Name)
{
    public override string ToString() => Name;
}

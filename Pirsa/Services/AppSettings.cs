using System.Text.Json;

namespace Pirsa.Services;

/// <summary>
/// Player preferences, persisted locally next to the addon list.
/// </summary>
public sealed class AppSettings
{
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _loaded;
    private string? _path;

    /// <summary>When false, the player always asks which source to use.</summary>
    public bool Autoplay { get; set; }

    /// <summary>Empty means "best available"; otherwise 4K, 1440p, 1080p, 720p or 480p.</summary>
    public string PreferredQuality { get; set; } = "";

    /// <summary>When true, the next episode starts as soon as the current one ends.</summary>
    public bool AutoAdvance { get; set; } = true;

    /// <summary>
    /// Seconds to wait after an episode ends before auto-advance starts the
    /// next one. Zero keeps the original "start now" behavior.
    /// </summary>
    public int AutoAdvanceDelaySeconds { get; set; }

    /// <summary>
    /// When false, Play and Continue watching always start at 0 and the details
    /// page does not advertise a resume position.
    /// </summary>
    public bool ResumePlayback { get; set; } = true;

    /// <summary>
    /// When true, the player looks up intro, recap and outro segments and shows
    /// a skip button while one is playing.
    /// </summary>
    public bool SkipSegmentsEnabled { get; set; } = true;

    /// <summary>Seek past a detected opening without waiting for a press.</summary>
    public bool AutoSkipIntro { get; set; }

    /// <summary>Seek past a "previously on…" recap without waiting for a press.</summary>
    public bool AutoSkipRecap { get; set; }

    /// <summary>Seek past an episode ending or a film's end credits.</summary>
    public bool AutoSkipOutro { get; set; }

    /// <summary>
    /// Playback backend id, or empty for Automatic (mpv when available, then
    /// libVLC). Resolved through <see cref="Pirsa.Playback.PlaybackEngines"/> so
    /// an unavailable choice falls back cleanly.
    /// </summary>
    public string PlaybackEngine { get; set; } = "";

    /// <summary>
    /// Canonical language code for subtitles that should turn on automatically,
    /// or empty when nothing is preferred. Set from Settings or by picking a
    /// language in the player.
    /// </summary>
    public string PreferredSubtitleLanguage { get; set; } = "";

    /// <summary>
    /// Canonical language code for the audio track to prefer, or empty for the
    /// track the release marks as default/original.
    /// </summary>
    public string PreferredAudioLanguage { get; set; } = "";

    /// <summary>1.0 is the player default; 0.85, 1.2 and 1.45 are the presets.</summary>
    public double SubtitleTextScale { get; set; } = 1.0;

    /// <summary>None, Dim or Solid.</summary>
    public string SubtitleBackground { get; set; } = "Dim";

    /// <summary>System, Light or Dark. System follows Windows.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Home, Discover or Watchlist. Applied on the next launch.</summary>
    public string StartPage { get; set; } = "Home";

    /// <summary>When false, Pirsa does not check GitHub Releases at launch.</summary>
    public bool CheckForUpdates { get; set; } = true;

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
        {
            return;
        }

        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
            {
                return;
            }

            try
            {
                var path = GetSettingsPath();
                if (File.Exists(path))
                {
                    var record = JsonSerializer.Deserialize<SettingsRecord>(
                        await File.ReadAllTextAsync(path, cancellationToken));

                    if (record is not null)
                    {
                        Autoplay = record.Autoplay;
                        PreferredQuality = record.PreferredQuality ?? "";
                        AutoAdvance = record.AutoAdvance;
                        AutoAdvanceDelaySeconds = Math.Clamp(record.AutoAdvanceDelaySeconds, 0, 30);
                        ResumePlayback = record.ResumePlayback;
                        SkipSegmentsEnabled = record.SkipSegmentsEnabled;
                        AutoSkipIntro = record.AutoSkipIntro;
                        AutoSkipRecap = record.AutoSkipRecap;
                        AutoSkipOutro = record.AutoSkipOutro;
                        PlaybackEngine = record.PlaybackEngine ?? "";
                        PreferredSubtitleLanguage = record.PreferredSubtitleLanguage ?? "";
                        PreferredAudioLanguage = record.PreferredAudioLanguage ?? "";
                        SubtitleTextScale = record.SubtitleTextScale <= 0
                            ? 1.0
                            : record.SubtitleTextScale;
                        SubtitleBackground = string.IsNullOrWhiteSpace(record.SubtitleBackground)
                            ? "Dim"
                            : record.SubtitleBackground;
                        Theme = string.IsNullOrWhiteSpace(record.Theme) ? "System" : record.Theme;
                        StartPage = string.IsNullOrWhiteSpace(record.StartPage) ? "Home" : record.StartPage;
                        CheckForUpdates = record.CheckForUpdates;
                    }
                }
            }
            catch
            {
                // Fall back to the defaults.
            }

            _loaded = true;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// Restores every preference on the Settings pages. Add-ons, watch history,
    /// the watchlist and sync credentials are intentionally untouched.
    /// </summary>
    public async Task ResetToDefaultsAsync(CancellationToken cancellationToken = default)
    {
        Autoplay = false;
        PreferredQuality = "";
        AutoAdvance = true;
        AutoAdvanceDelaySeconds = 0;
        ResumePlayback = true;
        SkipSegmentsEnabled = true;
        AutoSkipIntro = false;
        AutoSkipRecap = false;
        AutoSkipOutro = false;
        PlaybackEngine = "";
        PreferredSubtitleLanguage = "";
        PreferredAudioLanguage = "";
        SubtitleTextScale = 1.0;
        SubtitleBackground = "Dim";
        Theme = "System";
        StartPage = "Home";
        CheckForUpdates = true;

        await SaveAsync(cancellationToken);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var path = GetSettingsPath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var record = new SettingsRecord
            {
                Autoplay = Autoplay,
                PreferredQuality = PreferredQuality ?? "",
                AutoAdvance = AutoAdvance,
                AutoAdvanceDelaySeconds = Math.Clamp(AutoAdvanceDelaySeconds, 0, 30),
                ResumePlayback = ResumePlayback,
                SkipSegmentsEnabled = SkipSegmentsEnabled,
                AutoSkipIntro = AutoSkipIntro,
                AutoSkipRecap = AutoSkipRecap,
                AutoSkipOutro = AutoSkipOutro,
                PlaybackEngine = PlaybackEngine ?? "",
                PreferredSubtitleLanguage = PreferredSubtitleLanguage ?? "",
                PreferredAudioLanguage = PreferredAudioLanguage ?? "",
                SubtitleTextScale = SubtitleTextScale <= 0 ? 1.0 : SubtitleTextScale,
                SubtitleBackground = string.IsNullOrWhiteSpace(SubtitleBackground)
                    ? "Dim"
                    : SubtitleBackground,
                Theme = Theme ?? "System",
                StartPage = StartPage ?? "Home",
                CheckForUpdates = CheckForUpdates,
            };

            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
        }
        catch
        {
            // Settings stay in memory if persistence fails.
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private string GetSettingsPath()
    {
        if (_path is not null)
        {
            return _path;
        }

        _path = Path.Combine(LocalPaths.Root, "settings.json");
        return _path;
    }

    private sealed class SettingsRecord
    {
        public bool Autoplay { get; set; }

        public string PreferredQuality { get; set; } = "";

        public bool AutoAdvance { get; set; } = true;

        public int AutoAdvanceDelaySeconds { get; set; }

        public bool ResumePlayback { get; set; } = true;

        public bool SkipSegmentsEnabled { get; set; } = true;

        public bool AutoSkipIntro { get; set; }

        public bool AutoSkipRecap { get; set; }

        public bool AutoSkipOutro { get; set; }

        public string PlaybackEngine { get; set; } = "";

        public string PreferredSubtitleLanguage { get; set; } = "";

        public string PreferredAudioLanguage { get; set; } = "";

        public double SubtitleTextScale { get; set; } = 1.0;

        public string SubtitleBackground { get; set; } = "Dim";

        public string Theme { get; set; } = "System";

        public string StartPage { get; set; } = "Home";

        public bool CheckForUpdates { get; set; } = true;
    }
}

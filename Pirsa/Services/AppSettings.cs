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
    /// Playback backend id, or empty for the default. Resolved through
    /// <see cref="Pirsa.Playback.PlaybackEngines"/> so an unavailable choice
    /// falls back cleanly.
    /// </summary>
    public string PlaybackEngine { get; set; } = "";

    /// <summary>
    /// Canonical language code for subtitles that should turn on automatically,
    /// or empty when nothing is preferred. Set from Settings or by picking a
    /// language in the player.
    /// </summary>
    public string PreferredSubtitleLanguage { get; set; } = "";

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
                        PlaybackEngine = record.PlaybackEngine ?? "";
                        PreferredSubtitleLanguage = record.PreferredSubtitleLanguage ?? "";
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
                PlaybackEngine = PlaybackEngine ?? "",
                PreferredSubtitleLanguage = PreferredSubtitleLanguage ?? "",
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

        public string PlaybackEngine { get; set; } = "";

        public string PreferredSubtitleLanguage { get; set; } = "";
    }
}

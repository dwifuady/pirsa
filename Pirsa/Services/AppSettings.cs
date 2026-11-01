using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Storage;

namespace Pirsa.Services;

public enum AutoplayMode
{
    Ask,
    FirstAvailable,
    Pattern,
}

/// <summary>
/// Player preferences, persisted locally next to the addon list.
/// </summary>
public sealed class AppSettings
{
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _loaded;
    private string? _path;

    public AutoplayMode AutoplayMode { get; set; } = AutoplayMode.Ask;

    public string AutoplayPattern { get; set; } = "";

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
                        if (Enum.TryParse<AutoplayMode>(record.AutoplayMode, true, out var mode))
                        {
                            AutoplayMode = mode;
                        }

                        AutoplayPattern = record.AutoplayPattern ?? "";
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
                AutoplayMode = AutoplayMode.ToString(),
                AutoplayPattern = AutoplayPattern ?? "",
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

    public bool MatchesPattern(string text)
    {
        if (string.IsNullOrWhiteSpace(AutoplayPattern) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            return Regex.IsMatch(
                text,
                AutoplayPattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public static bool TryValidatePattern(string? pattern, out string error)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "Enter a pattern, for example 1080 or 1080|1440.";
            return false;
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
            error = "";
            return true;
        }
        catch (ArgumentException exception)
        {
            error = $"Invalid pattern: {exception.Message}";
            return false;
        }
    }

    private string GetSettingsPath()
    {
        if (_path is not null)
        {
            return _path;
        }

        try
        {
            _path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "settings.json");
        }
        catch
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Pirsa");
            _path = Path.Combine(directory, "settings.json");
        }

        return _path;
    }

    private sealed class SettingsRecord
    {
        public string AutoplayMode { get; set; } = nameof(Services.AutoplayMode.Ask);

        public string AutoplayPattern { get; set; } = "";
    }
}

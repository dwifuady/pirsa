using System.Text.Json;

namespace Pirsa.Services;

/// <summary>
/// One title's sticky subtitle language, stored as a canonical ISO 639 code.
/// </summary>
public sealed class SubtitleChoiceEntry
{
    public string TitleId { get; set; } = "";

    public string LanguageCode { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Per-title subtitle language, persisted in <c>subtitle-choices.json</c>.
/// Picking a language in the player applies to that movie or series, not to the
/// app-wide preferred language: watching one film with French subtitles must not
/// change the default for everything else. A series shares its id with every
/// episode, so the next episode keeps the same language.
/// </summary>
public sealed class SubtitleChoiceStore
{
    private const int MaxEntries = 500;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Dictionary<string, SubtitleChoiceEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private Task? _loadTask;
    private string? _storagePath;

    public Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        Task load;
        lock (_lock)
        {
            load = _loadTask ??= LoadCoreAsync();
        }

        return load.WaitAsync(cancellationToken);
    }

    /// <summary>The language picked for one title, or null when it follows the app default.</summary>
    public async Task<string?> GetLanguageAsync(
        string? titleId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return null;
        }

        await EnsureLoadedAsync(cancellationToken);

        lock (_lock)
        {
            return _entries.TryGetValue(titleId, out var entry) ? entry.LanguageCode : null;
        }
    }

    /// <summary>Remembers a language for one title. Saving twice the same value does no work.</summary>
    public async Task SetLanguageAsync(
        string? titleId,
        string? languageCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(titleId) || string.IsNullOrWhiteSpace(languageCode))
        {
            return;
        }

        await EnsureLoadedAsync(cancellationToken);

        lock (_lock)
        {
            if (_entries.TryGetValue(titleId, out var existing) &&
                existing.LanguageCode.Equals(languageCode, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _entries[titleId] = new SubtitleChoiceEntry
            {
                TitleId = titleId,
                LanguageCode = languageCode,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            TrimLocked();
        }

        await SaveCoreAsync(cancellationToken);
    }

    /// <summary>Drops one title's choice, so it follows the app preference again.</summary>
    public async Task ClearLanguageAsync(
        string? titleId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return;
        }

        await EnsureLoadedAsync(cancellationToken);

        lock (_lock)
        {
            if (!_entries.Remove(titleId))
            {
                return;
            }
        }

        await SaveCoreAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        lock (_lock)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            _entries.Clear();
        }

        await SaveCoreAsync(cancellationToken);
    }

    private void TrimLocked()
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        foreach (var stale in _entries.Values
                     .OrderByDescending(entry => entry.UpdatedAt)
                     .Skip(MaxEntries)
                     .ToList())
        {
            _entries.Remove(stale.TitleId);
        }
    }

    private async Task LoadCoreAsync()
    {
        var path = GetStoragePath();

        try
        {
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path);
                var entries = JsonSerializer.Deserialize<List<SubtitleChoiceEntry>>(json) ?? [];

                lock (_lock)
                {
                    foreach (var entry in entries
                                 .Where(entry =>
                                     !string.IsNullOrWhiteSpace(entry.TitleId) &&
                                     !string.IsNullOrWhiteSpace(entry.LanguageCode))
                                 .OrderByDescending(entry => entry.UpdatedAt)
                                 .Take(MaxEntries))
                    {
                        _entries[entry.TitleId] = entry;
                    }
                }
            }
        }
        catch
        {
            // A damaged file must not block playback; every title simply
            // follows the app-wide preference.
            lock (_lock)
            {
                _entries.Clear();
            }
        }
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            List<SubtitleChoiceEntry> snapshot;
            lock (_lock)
            {
                snapshot = _entries.Values
                    .OrderByDescending(entry => entry.UpdatedAt)
                    .ToList();
            }

            var path = GetStoragePath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(
                    snapshot,
                    new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
        }
        catch
        {
            // Choices stay in memory if persistence fails.
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private string GetStoragePath()
    {
        if (_storagePath is not null)
        {
            return _storagePath;
        }

        _storagePath = Path.Combine(LocalPaths.Root, "subtitle-choices.json");
        return _storagePath;
    }
}

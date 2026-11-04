using System.Text.Json;

namespace Pirsa.Services;

/// <summary>
/// One title's sticky subtitle timing adjustment, in milliseconds. Positive
/// values show subtitles later; negative values show them earlier.
/// </summary>
public sealed class SubtitleOffsetEntry
{
    public string TitleId { get; set; } = "";

    public long OffsetMs { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Per-title subtitle timing, persisted in <c>subtitle-offsets.json</c>. Addon
/// subtitle files are routinely a second or two out of sync, and the offset for
/// a release is worth keeping: the next episode, a source switch or a rewatch
/// should not need the same fix again.
/// </summary>
public sealed class SubtitleOffsetStore
{
    private const int MaxEntries = 500;

    /// <summary>Beyond a minute the file belongs to a different release.</summary>
    private const long MaxMagnitudeMs = 60_000;

    /// <summary>Holding an adjustment keys writes together instead of one per step.</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly object _lock = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Dictionary<string, SubtitleOffsetEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private Task? _loadTask;
    private string? _storagePath;
    private Timer? _saveTimer;
    private bool _dirty;

    public Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        Task load;
        lock (_lock)
        {
            load = _loadTask ??= LoadCoreAsync();
        }

        return load.WaitAsync(cancellationToken);
    }

    /// <summary>The remembered offset for one movie or series, or 0 when none.</summary>
    public async Task<long> GetDelayAsync(string? titleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return 0;
        }

        await EnsureLoadedAsync(cancellationToken);

        lock (_lock)
        {
            return _entries.TryGetValue(titleId, out var entry) ? entry.OffsetMs : 0;
        }
    }

    /// <summary>
    /// Remembers an offset. The write is coalesced so holding the timing
    /// buttons does not rewrite the file on every step; reads always see the
    /// newest value because it is already in memory.
    /// </summary>
    public async Task SetDelayAsync(
        string? titleId,
        long offsetMs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return;
        }

        await EnsureLoadedAsync(cancellationToken);

        offsetMs = Math.Clamp(offsetMs, -MaxMagnitudeMs, MaxMagnitudeMs);

        lock (_lock)
        {
            if (offsetMs == 0)
            {
                _entries.Remove(titleId);
            }
            else
            {
                _entries[titleId] = new SubtitleOffsetEntry
                {
                    TitleId = titleId,
                    OffsetMs = offsetMs,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };

                TrimLocked();
            }

            _dirty = true;
        }

        ScheduleSave();
    }

    /// <summary>Writes a pending adjustment now; used when playback is closed.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!_dirty)
        {
            return;
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
            _dirty = true;
        }

        _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        await SaveCoreAsync(cancellationToken);
    }

    private void ScheduleSave()
    {
        lock (_lock)
        {
            _saveTimer ??= new Timer(
                _ => _ = SaveCoreAsync(),
                null,
                Timeout.Infinite,
                Timeout.Infinite);
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
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
                var entries = JsonSerializer.Deserialize<List<SubtitleOffsetEntry>>(json) ?? [];

                lock (_lock)
                {
                    foreach (var entry in entries
                                 .Where(entry => !string.IsNullOrWhiteSpace(entry.TitleId))
                                 .OrderByDescending(entry => entry.UpdatedAt)
                                 .Take(MaxEntries))
                    {
                        entry.OffsetMs = Math.Clamp(entry.OffsetMs, -MaxMagnitudeMs, MaxMagnitudeMs);
                        _entries[entry.TitleId] = entry;
                    }
                }
            }
        }
        catch
        {
            // A damaged file must not block playback; offsets simply start empty.
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
            List<SubtitleOffsetEntry> snapshot;
            lock (_lock)
            {
                _dirty = false;
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
            // Offsets stay in memory if persistence fails.
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

        _storagePath = Path.Combine(LocalPaths.Root, "subtitle-offsets.json");
        return _storagePath;
    }
}

using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Pirsa.Services;

/// <summary>One submitted search query, shown as a title-bar suggestion.</summary>
public sealed class SearchHistoryEntry : ObservableObject
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = "";

    [JsonPropertyName("searchedAt")]
    public DateTimeOffset SearchedAt { get; set; }

    public override string ToString() => Query;
}

/// <summary>
/// The local search history behind the title-bar suggestions. A query is
/// recorded when it is submitted or when a search result is opened — not while
/// the user is still typing. Stored in <c>search-history.json</c>.
/// </summary>
public sealed class SearchHistoryStore
{
    private const int MaxEntries = 20;
    private const int MaxQueryLength = 200;
    private const int SuggestionLimit = 8;

    private readonly object _loadLock = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Task? _loadTask;
    private string? _storagePath;

    /// <summary>Submitted queries, newest first.</summary>
    public ObservableCollection<SearchHistoryEntry> Entries { get; } = [];

    /// <summary>Raised after the list changed and was persisted.</summary>
    public event EventHandler? Changed;

    public Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        Task load;
        lock (_loadLock)
        {
            load = _loadTask ??= LoadCoreAsync();
        }

        return load.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Saves a query for later suggestions. Repeating a query moves it back to
    /// the top instead of creating a duplicate.
    /// </summary>
    public async Task RecordAsync(string? query, CancellationToken cancellationToken = default)
    {
        var text = Normalize(query);
        if (text.Length == 0)
        {
            return;
        }

        await EnsureLoadedAsync(cancellationToken);

        if (Entries.FirstOrDefault(entry =>
                entry.Query.Equals(text, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            Entries.Remove(existing);
        }

        Entries.Insert(0, new SearchHistoryEntry
        {
            Query = text,
            SearchedAt = DateTimeOffset.UtcNow,
        });

        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }

        await SaveCoreAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// History entries that match the current text: prefix matches first, then
    /// other matches, newest first within each group. Empty text lists recent
    /// searches.
    /// </summary>
    public IReadOnlyList<SearchHistoryEntry> GetSuggestions(string? text)
    {
        var query = Normalize(text);

        var candidates = query.Length == 0
            ? Entries.ToList()
            : Entries
                .Where(entry => entry.Query.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        return candidates
            .OrderByDescending(entry =>
                query.Length > 0 && entry.Query.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(entry => entry.SearchedAt)
            .Take(SuggestionLimit)
            .ToList();
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        if (Entries.Count == 0)
        {
            return;
        }

        Entries.Clear();
        await SaveCoreAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Collapses whitespace so "  the   wire " and "the wire" match.</summary>
    private static string Normalize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "";
        }

        var text = string.Join(
            ' ',
            query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return text.Length > MaxQueryLength ? text[..MaxQueryLength].TrimEnd() : text;
    }

    private async Task LoadCoreAsync()
    {
        var path = GetStoragePath();

        try
        {
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path);
                var entries = JsonSerializer.Deserialize<List<SearchHistoryEntry>>(json) ?? [];

                foreach (var entry in entries
                             .Where(entry => !string.IsNullOrWhiteSpace(entry.Query))
                             .OrderByDescending(entry => entry.SearchedAt)
                             .Take(MaxEntries))
                {
                    Entries.Add(entry);
                }
            }
        }
        catch
        {
            // A damaged history file must not block searching.
            Entries.Clear();
        }
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var path = GetStoragePath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(
                    Entries.ToList(),
                    new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
        }
        catch
        {
            // Suggestions stay in memory if persistence fails.
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

        _storagePath = Path.Combine(LocalPaths.Root, "search-history.json");
        return _storagePath;
    }
}

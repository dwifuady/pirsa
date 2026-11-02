using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Pirsa.Services;

namespace Pirsa.ViewModels;

/// <summary>
/// The watchlist page: every title saved from a details page, newest first.
/// </summary>
public partial class LibraryViewModel : ObservableObject
{
    private readonly WatchlistStore _watchlist;

    public LibraryViewModel(WatchlistStore watchlist)
    {
        _watchlist = watchlist;
    }

    public ObservableCollection<WatchlistEntry> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public bool IsEmpty => !IsLoading && Items.Count == 0;

    public bool HasItems => Items.Count > 0;

    public string CountText => Items.Count == 1 ? "1 title" : $"{Items.Count} titles";

    /// <summary>
    /// Reads the local watchlist. Cheap enough to run on every visit, which
    /// keeps the page current after saving a title from its details page.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        try
        {
            await _watchlist.EnsureLoadedAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            IsLoading = false;
            RaiseListInfo();
        }

        Items.Clear();
        foreach (var entry in _watchlist.Entries.OrderByDescending(entry => entry.AddedAt))
        {
            Items.Add(entry);
        }

        RaiseListInfo();
    }

    public async Task RemoveAsync(WatchlistEntry entry, CancellationToken cancellationToken = default)
    {
        await _watchlist.RemoveAsync(entry.MetaId, cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _watchlist.ClearAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    private void RaiseListInfo()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CountText));
    }
}

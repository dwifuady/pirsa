using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.Helpers;
using Windows.System;

namespace Pirsa.Views;

/// <summary>
/// The Settings shell. A native category pane on the left, one focused page on
/// the right, and a search index over every setting.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private static readonly Dictionary<string, Type> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["general"] = typeof(GeneralSettingsPage),
        ["playback"] = typeof(PlaybackSettingsPage),
        ["subtitles"] = typeof(SubtitlesSettingsPage),
        ["library"] = typeof(LibrarySettingsPage),
        ["addons"] = typeof(AddonsSettingsPage),
        ["sync"] = typeof(SyncSettingsPage),
        ["about"] = typeof(AboutSettingsPage),
    };

    private static string _lastCategory = "playback";
    private bool _syncingSelection;
    private string? _pendingAnchor;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is SettingsMessage message)
        {
            NavigateToCategory("addons", message);
            return;
        }

        if (e.Parameter is string tag && Categories.ContainsKey(tag))
        {
            NavigateToCategory(tag);
            return;
        }

        NavigateToCategory(_lastCategory);
    }

    /// <summary>
    /// Shows an add-on link result on the Add-ons page, navigating there when
    /// Settings was opened from elsewhere.
    /// </summary>
    public void ShowAddonMessage(SettingsMessage message)
    {
        SettingsSearch.Text = "";
        HideSearch();

        if (SettingsFrame.Content is AddonsSettingsPage addons)
        {
            addons.ShowMessage(message);
            SelectCategory("addons");
            return;
        }

        NavigateToCategory("addons", message);
    }

    private void NavigateToCategory(string tag, SettingsMessage? message = null, string? anchor = null)
    {
        if (!Categories.TryGetValue(tag, out var pageType))
        {
            return;
        }

        _lastCategory = tag;
        SettingsSearch.Text = "";
        HideSearch();
        SelectCategory(tag);

        if (SettingsFrame.CurrentSourcePageType == pageType)
        {
            if (message is not null && SettingsFrame.Content is AddonsSettingsPage addons)
            {
                addons.ShowMessage(message);
            }

            if (!string.IsNullOrWhiteSpace(anchor))
            {
                HighlightAnchor(anchor);
            }

            return;
        }

        // Let Navigated highlight the row once the page is in the frame.
        _pendingAnchor = anchor;
        SettingsFrame.Navigate(pageType, message, new SuppressNavigationTransitionInfo());
        SettingsFrame.BackStack.Clear();
        BackButton.Visibility = SettingsFrame.CanGoBack
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SelectCategory(string tag)
    {
        var item = SettingsNav.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate =>
                candidate.Tag is string candidateTag &&
                candidateTag.Equals(tag, StringComparison.OrdinalIgnoreCase));

        if (item is null || ReferenceEquals(SettingsNav.SelectedItem, item))
        {
            return;
        }

        _syncingSelection = true;
        SettingsNav.SelectedItem = item;
        _syncingSelection = false;
    }

    private void SettingsNav_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection)
        {
            return;
        }

        if (args.SelectedItemContainer?.Tag is string tag)
        {
            NavigateToCategory(tag);
        }
    }

    private void SettingsFrame_Navigated(object sender, NavigationEventArgs e)
    {
        BackButton.Visibility = SettingsFrame.CanGoBack
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Home rows is an editor reached from Library; keep Library selected.
        var tag = e.SourcePageType == typeof(HomeSettingsPage)
            ? "library"
            : Categories.FirstOrDefault(pair => pair.Value == e.SourcePageType).Key;

        if (!string.IsNullOrEmpty(tag))
        {
            SelectCategory(tag);
        }

        if (_pendingAnchor is { } pendingAnchor)
        {
            _pendingAnchor = null;
            HighlightAnchor(pendingAnchor);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsFrame.CanGoBack)
        {
            SettingsFrame.GoBack();
        }
    }

    private void SettingsSearch_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        var query = sender.Text.Trim();
        if (query.Length == 0)
        {
            HideSearch();
            return;
        }

        var results = SettingsSearchIndex.Search(query);
        SearchResults.ItemsSource = results;
        SearchSummary.Text = results.Count switch
        {
            0 => "No settings match.",
            1 => "1 setting",
            _ => $"{results.Count} settings",
        };
        NoResults.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchOverlay.Visibility = Visibility.Visible;
    }

    private void SettingsSearch_QuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (SearchResults.Items.Count > 0 &&
            SearchResults.Items[0] is SettingsSearchEntry first)
        {
            OpenSearchResult(first);
        }
    }

    private void SettingsSearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        SettingsSearch.Text = "";
        HideSearch();
        SettingsSearch.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void SearchResults_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SettingsSearchEntry entry)
        {
            OpenSearchResult(entry);
        }
    }

    private void OpenSearchResult(SettingsSearchEntry entry)
    {
        SettingsSearch.Text = "";
        HideSearch();
        NavigateToCategory(entry.PageTag, anchor: entry.Anchor);
    }

    private void HideSearch()
    {
        SearchOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Opens any collapsed Advanced sections, then brings the named row into
    /// view. Pages name their rows so the search index stays declarative.
    /// </summary>
    private async void HighlightAnchor(string anchor)
    {
        // Navigation can complete before the new page has measured, and an
        // Expander's content is only realized after it opens. Retry briefly so
        // a search result always lands on the actual row.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (SettingsFrame.Content is not FrameworkElement root)
            {
                await Task.Delay(60);
                continue;
            }

            foreach (var expander in FindDescendants<Expander>(root))
            {
                expander.IsExpanded = true;
            }

            if (root.FindName(anchor) is FrameworkElement target && target.IsLoaded)
            {
                target.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = true,
                    VerticalAlignmentRatio = 0.3,
                });
                return;
            }

            await Task.Delay(60);
        }
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}

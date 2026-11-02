using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.Helpers;
using Pirsa.Services;
using Pirsa.ViewModels;

namespace Pirsa.Views;

/// <summary>
/// The watchlist: titles saved from a details page, with a way back into
/// their details and a way to unsave them.
/// </summary>
public sealed partial class LibraryPage : Page
{
    private const string PosterAnimationKey = "PosterAnimation";

    public LibraryViewModel ViewModel { get; } = new(App.Watchlist);

    public LibraryPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Titles are saved while the details page is open, so read the store
        // again on every visit instead of relying on cached rows.
        await ViewModel.RefreshAsync();
    }

    private void Items_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WatchlistEntry entry)
        {
            return;
        }

        Image? poster = null;
        if (sender is GridView gridView &&
            gridView.ContainerFromItem(entry) is GridViewItem container)
        {
            poster = VisualTreeHelpers.FindDescendant<Image>(container);
        }

        var meta = entry.ToMetaItem();
        if (poster is not null)
        {
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate(PosterAnimationKey, poster);
            Frame.Navigate(typeof(DetailsPage), meta, new SuppressNavigationTransitionInfo());
        }
        else
        {
            Frame.Navigate(typeof(DetailsPage), meta, new DrillInNavigationTransitionInfo());
        }
    }

    /// <summary>
    /// Right-click or the menu key on a card offers to unsave that title. The
    /// flyout is built here so it knows which card it was opened on.
    /// </summary>
    private void Card_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not WatchlistEntry entry)
        {
            return;
        }

        args.Handled = true;

        var remove = new MenuFlyoutItem
        {
            Text = "Remove from watchlist",
            Icon = new SymbolIcon(Symbol.Delete),
        };
        remove.Click += async (_, _) => await ViewModel.RemoveAsync(entry);

        var flyout = new MenuFlyout();
        flyout.Items.Add(remove);

        if (args.TryGetPosition(element, out var position))
        {
            flyout.ShowAt(element, position);
        }
        else
        {
            flyout.ShowAt(element);
        }
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear watchlist?",
            Content = "This removes every saved title from this device. Your watch history and addons stay as they are.",
            PrimaryButtonText = "Clear watchlist",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ClearAsync();
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(DiscoverPage), null, new DrillInNavigationTransitionInfo());
    }
}

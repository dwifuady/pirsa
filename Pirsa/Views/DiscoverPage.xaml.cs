using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.Helpers;
using Pirsa.Models;
using Pirsa.ViewModels;

namespace Pirsa.Views;

/// <summary>
/// Discover: type / catalog / genre filters plus optional search over the
/// catalogs configured addons expose.
/// </summary>
public sealed partial class DiscoverPage : Page
{
    private const string PosterAnimationKey = "PosterAnimation";

    private bool _navigated;
    private int _loadedRevision = -1;

    public DiscoverViewModel ViewModel { get; } = new(App.Addons);

    public DiscoverPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (_navigated && _loadedRevision == App.Addons.Revision)
        {
            return;
        }

        _navigated = true;
        _loadedRevision = App.Addons.Revision;
        await ViewModel.InitializeAsync();
    }

    private void Results_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not MetaItem meta)
        {
            return;
        }

        // Opening a search result makes the query worth remembering.
        if (ViewModel.IsSearching)
        {
            _ = App.SearchHistory.RecordAsync(ViewModel.SearchText);
        }

        Image? poster = null;
        if (sender is GridView gridView &&
            gridView.ContainerFromItem(meta) is GridViewItem container)
        {
            poster = VisualTreeHelpers.FindDescendant<Image>(container);
        }

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

    private void ManageAddons_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(SettingsPage));
    }
}

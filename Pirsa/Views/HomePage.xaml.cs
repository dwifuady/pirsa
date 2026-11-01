using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.Helpers;
using Pirsa.Models;
using Pirsa.ViewModels;

namespace Pirsa.Views;

/// <summary>
/// The home feed: Top 10 hero plus one horizontal rail per addon catalog.
/// </summary>
public sealed partial class HomePage : Page
{
    private const string PosterAnimationKey = "PosterAnimation";

    private bool _loaded;
    private bool _syncingHero;
    private int _loadedRevision = -1;

    public HomeViewModel ViewModel { get; } = new(App.Addons);

    public HomePage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (_loaded && _loadedRevision == App.Addons.Revision)
        {
            return;
        }

        _loaded = true;
        await ViewModel.LoadAsync();
        _loadedRevision = App.Addons.Revision;
        SyncHeroSelection();
    }

    private void ManageAddons_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(SettingsPage));
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeViewModel.HeroSlides) && !ViewModel.IsLoading)
        {
            SyncHeroSelection();
        }
    }

    private void SyncHeroSelection()
    {
        if (ViewModel.HeroSlides <= 0)
        {
            return;
        }

        _syncingHero = true;
        try
        {
            HeroFlip.SelectedIndex = 0;
            HeroPager.SelectedPageIndex = 0;
        }
        finally
        {
            _syncingHero = false;
        }
    }

    private void HeroFlip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingHero || HeroPager is null)
        {
            return;
        }

        _syncingHero = true;
        try
        {
            if (HeroFlip.SelectedIndex >= 0)
            {
                HeroPager.SelectedPageIndex = HeroFlip.SelectedIndex;
            }
        }
        finally
        {
            _syncingHero = false;
        }
    }

    private void HeroPager_SelectedIndexChanged(PipsPager sender, PipsPagerSelectedIndexChangedEventArgs args)
    {
        if (_syncingHero)
        {
            return;
        }

        var index = sender.SelectedPageIndex;
        if (index < 0 || index >= ViewModel.HeroItems.Count)
        {
            return;
        }

        _syncingHero = true;
        try
        {
            HeroFlip.SelectedIndex = index;
        }
        finally
        {
            _syncingHero = false;
        }
    }

    private void Catalog_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not MetaItem meta)
        {
            return;
        }

        Image? poster = null;
        if (sender is ListView listView &&
            listView.ContainerFromItem(meta) is ListViewItem container)
        {
            poster = VisualTreeHelpers.FindDescendant<Image>(container);
        }

        OpenDetails(meta, poster);
    }

    private void HeroInfo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HeroItemViewModel hero)
        {
            OpenDetails(hero.Meta, null);
        }
    }

    private void HeroPlay_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HeroItemViewModel hero)
        {
            Frame.Navigate(
                typeof(PlayerPage),
                PlaybackRequest.ForMeta(hero.Meta),
                new DrillInNavigationTransitionInfo());
        }
    }

    private void OpenDetails(MetaItem meta, UIElement? posterSource)
    {
        if (posterSource is not null)
        {
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate(PosterAnimationKey, posterSource);
            Frame.Navigate(typeof(DetailsPage), meta, new SuppressNavigationTransitionInfo());
        }
        else
        {
            Frame.Navigate(typeof(DetailsPage), meta, new DrillInNavigationTransitionInfo());
        }
    }
}

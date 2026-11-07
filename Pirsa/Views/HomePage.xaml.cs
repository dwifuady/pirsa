using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.Helpers;
using Pirsa.Models;
using Pirsa.Services;
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

    public HomeViewModel ViewModel { get; } = new(App.Addons, App.WatchProgress);

    public HomePage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // The watch history changes while the player is open, so refresh the
        // rail on every visit even when the cached catalogs stay as they are.
        await ViewModel.RefreshContinueWatchingAsync();

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

    private async void HeroPlay_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HeroItemViewModel hero)
        {
            return;
        }

        // Same guard as the details page: no stream addon means setup, not a
        // player error screen.
        if (!await StreamSetupPrompt.EnsurePlayableAsync(XamlRoot, OpenAddonSettings))
        {
            return;
        }

        Frame.Navigate(
            typeof(PlayerPage),
            PlaybackRequest.ForMeta(hero.Meta),
            new DrillInNavigationTransitionInfo());
    }

    private void OpenAddonSettings() => Frame.Navigate(typeof(SettingsPage));

    private void Continue_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WatchProgressEntry entry)
        {
            return;
        }

        // Continue watching is already a play intent: start the remembered
        // source without showing the picker, whatever the autoplay setting.
        Frame.Navigate(
            typeof(PlayerPage),
            entry.ToPlaybackRequest() with { AutoPlay = true },
            new DrillInNavigationTransitionInfo());
    }

    private void ContinueCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not WatchProgressEntry entry)
        {
            return;
        }

        e.Handled = true;

        var remove = new MenuFlyoutItem { Text = "Remove from Continue watching" };
        remove.Click += async (_, _) => await ViewModel.RemoveContinueWatchingAsync(entry);

        var flyout = new MenuFlyout();
        flyout.Items.Add(remove);
        flyout.ShowAt(element, e.GetPosition(element));
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

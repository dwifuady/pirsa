using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pirsa.ViewModels;

namespace Pirsa.Views;

/// <summary>
/// Settings → Home. Pins and orders the catalogs shown on the Home screen;
/// a flat list so rows from different addons can share one order.
/// </summary>
public sealed partial class HomeSettingsPage : Page
{
    public HomeSettingsViewModel ViewModel { get; } = new(App.Addons);

    public HomeSettingsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Detach();
    }

    private async void Pin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HomeCatalogOptionViewModel option)
        {
            await ViewModel.PinAsync(option);
        }
    }

    private async void Unpin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HomePinItemViewModel item)
        {
            await ViewModel.UnpinAsync(item);
        }
    }

    private async void MovePinUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HomePinItemViewModel item)
        {
            await ViewModel.MovePinnedAsync(item, -1);
        }
    }

    private async void MovePinDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HomePinItemViewModel item)
        {
            await ViewModel.MovePinnedAsync(item, 1);
        }
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.ViewModels;

namespace Pirsa.Views;

/// <summary>Settings → General. Appearance, startup and update checks.</summary>
public sealed partial class GeneralSettingsPage : Page
{
    public GeneralSettingsViewModel ViewModel { get; } = new(App.Settings);

    public GeneralSettingsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CheckNowAsync();
    }
}

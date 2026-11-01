using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.Models;
using Pirsa.ViewModels;

namespace Pirsa.Views;

/// <summary>
/// Movie / series details with a poster-to-hero connected animation.
/// </summary>
public sealed partial class DetailsPage : Page
{
    private const string PosterAnimationKey = "PosterAnimation";

    private ConnectedAnimation? _posterAnimation;

    public DetailsViewModel ViewModel { get; } = new(App.Addons);

    public DetailsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not MetaItem preview)
        {
            ViewModel.HasError = true;
            ViewModel.ErrorMessage = "This title is missing its details.";
            return;
        }

        // Show the catalog preview immediately, then refresh with full meta.
        ViewModel.Meta = preview;
        PreparePosterAnimation();

        await ViewModel.LoadAsync(preview.Type, preview.Id);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _posterAnimation = null;
    }

    private void PreparePosterAnimation()
    {
        _posterAnimation = ConnectedAnimationService
            .GetForCurrentView()
            .GetAnimation(PosterAnimationKey);

        if (_posterAnimation is null)
        {
            return;
        }

        _posterAnimation.Configuration = new BasicConnectedAnimationConfiguration();

        if (PosterImage.IsLoaded)
        {
            _posterAnimation.TryStart(PosterImage);
            _posterAnimation = null;
        }
        else
        {
            PosterImage.Loaded += PosterImage_Loaded;
        }
    }

    private void PosterImage_Loaded(object sender, RoutedEventArgs e)
    {
        PosterImage.Loaded -= PosterImage_Loaded;
        _posterAnimation?.TryStart(PosterImage);
        _posterAnimation = null;
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Meta is not { } meta)
        {
            return;
        }

        var firstEpisode = ViewModel.SelectedSeason?.Episodes.FirstOrDefault();
        Frame.Navigate(
            typeof(PlayerPage),
            PlaybackRequest.ForMeta(meta, firstEpisode),
            new DrillInNavigationTransitionInfo());
    }

    private void Episode_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Meta is not { } series ||
            ((FrameworkElement)sender).DataContext is not MetaVideo episode)
        {
            return;
        }

        Frame.Navigate(
            typeof(PlayerPage),
            PlaybackRequest.ForEpisode(series, episode),
            new DrillInNavigationTransitionInfo());
    }

    private async void Imdb_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(ViewModel.ImdbUrl, UriKind.Absolute, out var uri))
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }
}

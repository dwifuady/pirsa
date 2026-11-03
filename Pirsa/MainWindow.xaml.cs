using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.ViewModels;
using Pirsa.Views;
using Windows.Graphics;

namespace Pirsa;

/// <summary>
/// The app window: custom title bar, Store-style navigation pane, and the
/// frame that hosts Home, Discover, Watchlist, Details, Settings and the player.
/// </summary>
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private bool _playerFullscreen;
    private bool _initialNavigation = true;
    private DiscoverViewModel? _discoverViewModel;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Unpackaged builds have no manifest icon, and a relative path resolves
        // against the working directory, so use the deployed asset by full path.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        SizeAndCenterWindow();

        RootFrame.Navigated += RootFrame_Navigated;
        RootFrame.Navigate(typeof(HomePage));

        // The inner TextBox handles the click; still offer recent searches
        // when the box itself is pressed.
        AppSearchBox.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(AppSearchBox_PointerPressed),
            true);
    }

    private void RootFrame_Navigated(object sender, NavigationEventArgs e)
    {
        var pageType = RootFrame.CurrentSourcePageType;

        NavView.IsPaneVisible = pageType != typeof(PlayerPage);
        AppTitleBar.IsPaneToggleButtonVisible = pageType != typeof(PlayerPage);

        if (pageType == typeof(PlayerPage))
        {
            // Playback is immersive; return to the icon rail afterwards.
            NavView.IsPaneOpen = false;
        }

        if (pageType == typeof(SettingsPage))
        {
            NavView.SelectedItem = NavView.SettingsItem;
        }
        else if (pageType == typeof(HomePage))
        {
            NavView.SelectedItem = HomeNavItem;
        }
        else if (pageType == typeof(DiscoverPage))
        {
            NavView.SelectedItem = DiscoverNavItem;
        }
        else if (pageType == typeof(LibraryPage))
        {
            NavView.SelectedItem = WatchlistNavItem;
        }

        AppTitleBar.IsBackButtonVisible = RootFrame.CanGoBack;

        AttachDiscoverSearch();
        SyncTitleBarSearch();

        if (_initialNavigation)
        {
            _initialNavigation = false;
            DispatcherQueue.TryEnqueue(() => NavView.IsPaneOpen = false);
        }
    }

    /// <summary>
    /// The title-bar search box is app-wide. It follows the cached Discover
    /// page's view model so results, placeholder and text stay in sync.
    /// </summary>
    private void AttachDiscoverSearch()
    {
        if (RootFrame.Content is not DiscoverPage page ||
            ReferenceEquals(page.ViewModel, _discoverViewModel))
        {
            return;
        }

        if (_discoverViewModel is not null)
        {
            _discoverViewModel.PropertyChanged -= DiscoverViewModel_PropertyChanged;
        }

        _discoverViewModel = page.ViewModel;
        _discoverViewModel.PropertyChanged += DiscoverViewModel_PropertyChanged;
    }

    private void DiscoverViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiscoverViewModel.SearchText)
            or nameof(DiscoverViewModel.SearchPlaceholderText)
            or nameof(DiscoverViewModel.CanSearch))
        {
            SyncTitleBarSearch();
        }
    }

    private void SyncTitleBarSearch()
    {
        AppSearchBox.Visibility = RootFrame.CurrentSourcePageType == typeof(PlayerPage)
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (_discoverViewModel is not { } viewModel)
        {
            return;
        }

        if (!string.Equals(AppSearchBox.Text, viewModel.SearchText, StringComparison.Ordinal))
        {
            AppSearchBox.Text = viewModel.SearchText;
        }

        AppSearchBox.PlaceholderText = viewModel.SearchPlaceholderText;
    }

    private void AppSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            SubmitSearch(sender.Text);
        }

        UpdateSearchSuggestions(sender.Text);
    }

    private void AppSearchBox_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        UpdateSearchSuggestions(AppSearchBox.Text);

        if (AppSearchBox.ItemsSource is null)
        {
            return;
        }

        // Let the TextBox finish its own press handling before opening the list.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (AppSearchBox.ItemsSource is not null)
            {
                AppSearchBox.IsSuggestionListOpen = true;
            }
        });
    }

    private void AppSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // A suggestion list opened on click must not stay over the results.
        AppSearchBox.IsSuggestionListOpen = false;

        var text = (args.ChosenSuggestion as Services.SearchHistoryEntry)?.Query
            ?? args.QueryText;

        // Only submitted queries are remembered; live typing is not.
        _ = App.SearchHistory.RecordAsync(text);
        SubmitSearch(text, immediate: true);
    }

    /// <summary>
    /// Offers recent searches, filtered by what is already typed. The list is
    /// only assigned here, so it never opens on its own.
    /// </summary>
    private void UpdateSearchSuggestions(string? text)
    {
        var suggestions = App.SearchHistory.GetSuggestions(text);
        AppSearchBox.ItemsSource = suggestions.Count > 0 ? suggestions : null;
    }

    /// <summary>
    /// Search results live on Discover, so typing from any page switches there
    /// before applying the query. The view model holds the text, which lets a
    /// freshly created page search as part of its first load.
    /// </summary>
    private void SubmitSearch(string? text, bool immediate = false)
    {
        if (RootFrame.CurrentSourcePageType != typeof(DiscoverPage))
        {
            NavigateSection(typeof(DiscoverPage));
            AttachDiscoverSearch();
        }

        if (RootFrame.Content is DiscoverPage page)
        {
            page.ViewModel.SubmitSearch(text, immediate);
        }
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            NavigateSection(typeof(SettingsPage));
            NavView.IsPaneOpen = false;
            return;
        }

        if (ReferenceEquals(args.InvokedItemContainer, HomeNavItem))
        {
            NavigateSection(typeof(HomePage));
            NavView.IsPaneOpen = false;
            return;
        }

        if (ReferenceEquals(args.InvokedItemContainer, DiscoverNavItem))
        {
            NavigateSection(typeof(DiscoverPage));
            NavView.IsPaneOpen = false;
            return;
        }

        if (ReferenceEquals(args.InvokedItemContainer, WatchlistNavItem))
        {
            NavigateSection(typeof(LibraryPage));
            NavView.IsPaneOpen = false;
        }
    }

    /// <summary>
    /// Switch between top-level sections without stacking them on the back
    /// stack; the pane is the navigation. Detail and player pages still use
    /// normal frame back navigation.
    /// </summary>
    private void NavigateSection(Type pageType)
    {
        if (RootFrame.CurrentSourcePageType == pageType)
        {
            return;
        }

        RootFrame.BackStack.Clear();
        RootFrame.Navigate(pageType, null, new SuppressNavigationTransitionInfo());
    }

    private void AppTitleBar_BackRequested(TitleBar sender, object args)
    {
        if (RootFrame.CanGoBack)
        {
            RootFrame.GoBack();
        }
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    /// <summary>
    /// Enter/exit immersive playback. The player page owns the toggle, but the
    /// window owns the presenter and chrome.
    /// </summary>
    public void SetPlayerFullscreen(bool fullscreen)
    {
        if (_playerFullscreen == fullscreen)
        {
            return;
        }

        _playerFullscreen = fullscreen;

        if (fullscreen)
        {
            NavView.IsPaneVisible = false;
            AppTitleBar.Visibility = Visibility.Collapsed;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        else
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            AppTitleBar.Visibility = Visibility.Visible;
            NavView.IsPaneVisible = RootFrame.CurrentSourcePageType != typeof(PlayerPage);
            AppTitleBar.IsBackButtonVisible = RootFrame.CanGoBack;
        }
    }

    /// <summary>
    /// WinUI has no SizeToContent, so choose a media-app window size in DIPs,
    /// scale it to physical pixels, and keep it inside the work area.
    /// </summary>
    private void SizeAndCenterWindow()
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;

        const double widthDip = 1440;
        const double heightDip = 920;

        var width = (int)(widthDip * scale);
        var height = (int)(heightDip * scale);

        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        width = Math.Min(width, Math.Max(960, workArea.Width - 32));
        height = Math.Min(height, Math.Max(640, workArea.Height - 32));

        AppWindow.MoveAndResize(new RectInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2,
            width,
            height));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 620;
        }
    }
}

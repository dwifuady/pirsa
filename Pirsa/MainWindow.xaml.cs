using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.Views;
using Windows.Graphics;

namespace Pirsa;

/// <summary>
/// The app window: custom title bar, Store-style navigation pane, and the
/// frame that hosts Home, Details, Settings and the player.
/// </summary>
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private bool _playerFullscreen;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        SizeAndCenterWindow();

        RootFrame.Navigated += RootFrame_Navigated;
        RootFrame.Navigate(typeof(HomePage));
    }

    private void RootFrame_Navigated(object sender, NavigationEventArgs e)
    {
        var pageType = RootFrame.CurrentSourcePageType;

        NavView.IsPaneVisible = pageType != typeof(PlayerPage);

        if (pageType == typeof(SettingsPage))
        {
            NavView.SelectedItem = NavView.SettingsItem;
        }
        else if (pageType == typeof(HomePage))
        {
            NavView.SelectedItem = HomeNavItem;
        }

        AppTitleBar.IsBackButtonVisible = RootFrame.CanGoBack;
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            NavigateSection(typeof(SettingsPage));
            return;
        }

        if (ReferenceEquals(args.InvokedItemContainer, HomeNavItem))
        {
            NavigateSection(typeof(HomePage));
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

using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Pirsa.Playback;
using Pirsa.Services;

namespace Pirsa;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The user's configured addons. Cinemeta is the only default; stream
    /// addons are added from Settings → Addons.
    /// </summary>
    public static AddonRegistry Addons { get; } = new();

    /// <summary>
    /// Per-title stream discovery cache. The details page warms it so the
    /// player opens with sources ready.
    /// </summary>
    public static StreamDiscovery StreamDiscovery { get; } = new(Addons);

    /// <summary>Player and app preferences, persisted in settings.json.</summary>
    public static AppSettings Settings { get; } = new();

    /// <summary>Local watch history, persisted in watch-progress.json.</summary>
    public static WatchProgressStore WatchProgress { get; } = new();

    /// <summary>Saved titles, persisted in watchlist.json.</summary>
    public static WatchlistStore Watchlist { get; } = new();

    /// <summary>Submitted search queries, persisted in search-history.json.</summary>
    public static SearchHistoryStore SearchHistory { get; } = new();

    /// <summary>Per-title subtitle timing, persisted in subtitle-offsets.json.</summary>
    public static SubtitleOffsetStore SubtitleOffsets { get; } = new();

    /// <summary>Per-title subtitle language, persisted in subtitle-choices.json.</summary>
    public static SubtitleChoiceStore SubtitleChoices { get; } = new();

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    private static string? _pendingAddonLink;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// A <c>stremio://</c> addon link the app was launched with, if any. The
    /// window takes it once its content has loaded, which is when a dialog can
    /// be shown. Kept static because activation can arrive before any window.
    /// </summary>
    internal static string? TakePendingAddonLink()
    {
        var link = _pendingAddonLink;
        _pendingAddonLink = null;
        return link;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Read this before the window exists so a cold-start link is not lost.
        _pendingAddonLink = ReadLaunchAddonLink(args);

        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();

        // A running instance only receives this when activation is redirected
        // to it; without single-instancing Windows starts a second process,
        // which handles the link through the cold-start path above.
        AppInstance.GetCurrent().Activated += AppInstance_Activated;

        _ = Addons.EnsureLoadedAsync();
        _ = Settings.EnsureLoadedAsync();
        _ = WatchProgress.EnsureLoadedAsync();
        _ = Watchlist.EnsureLoadedAsync();
        _ = SearchHistory.EnsureLoadedAsync();
        _ = SubtitleOffsets.EnsureLoadedAsync();
        _ = SubtitleChoices.EnsureLoadedAsync();
        _ = WarmUpPlaybackAsync();
    }

    /// <summary>
    /// Warms the backend the user selected, after settings have loaded. Loading
    /// both in parallel would always warm the default.
    /// </summary>
    private static async Task WarmUpPlaybackAsync()
    {
        await Settings.EnsureLoadedAsync();
        await PlaybackEngines.WarmUpAsync(Settings.PlaybackEngine);
    }

    private static void AppInstance_Activated(object? sender, AppActivationArguments args)
    {
        if (TryGetProtocolLink(args) is not { } link)
        {
            return;
        }

        if (DispatcherQueue is null)
        {
            _pendingAddonLink = link;
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (Window is MainWindow window)
            {
                _ = window.HandleAddonLinkAsync(link);
            }
            else
            {
                _pendingAddonLink = link;
            }
        });
    }

    /// <summary>
    /// The link this launch was activated with. Packaged launches report a
    /// protocol activation, while unpackaged launchers pass the URI as a
    /// command-line argument — both are checked.
    /// </summary>
    private static string? ReadLaunchAddonLink(LaunchActivatedEventArgs args)
    {
        try
        {
            if (TryGetProtocolLink(AppInstance.GetCurrent().GetActivatedEventArgs()) is { } link)
            {
                return link;
            }
        }
        catch
        {
            // Some unpackaged hosts do not expose activation arguments; the
            // command line below is the fallback.
        }

        return FirstAddonLink(Environment.GetCommandLineArgs().Skip(1))
            ?? FirstAddonLink((args.Arguments ?? "").Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string? TryGetProtocolLink(AppActivationArguments? activation)
    {
        if (activation?.Kind != ExtendedActivationKind.Protocol ||
            activation.Data is not Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs protocol ||
            protocol.Uri is not { } uri)
        {
            return null;
        }

        return uri.OriginalString;
    }

    private static string? FirstAddonLink(IEnumerable<string> arguments) => arguments
        .Select(argument => argument.Trim('"', '\''))
        .FirstOrDefault(argument =>
            argument.StartsWith(StremioAddonLink.Scheme + ":", StringComparison.OrdinalIgnoreCase));
}

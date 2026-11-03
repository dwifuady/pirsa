using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Pirsa.Helpers;

/// <summary>
/// Keeps Play from becoming a dead end when no enabled addon can return video
/// sources. The setup is explained once per session, then Play goes straight to
/// Settings → Addons. This is also where the third-party addon model is stated:
/// Pirsa ships no stream addon and hosts no content.
/// </summary>
public static class StreamSetupPrompt
{
    private static bool _explained;
    private static bool _dialogOpen;

    /// <summary>
    /// Returns true when playback can proceed. Otherwise it explains the setup
    /// (first time) or opens the addon settings, and returns false.
    /// </summary>
    public static async Task<bool> EnsurePlayableAsync(XamlRoot? xamlRoot, Action openAddonSettings)
    {
        try
        {
            await App.Addons.EnsureLoadedAsync();
            await App.Addons.LoadManifestsAsync();
        }
        catch
        {
            // If the addon list cannot be inspected, let the player report the
            // failure; it has its own Manage addons path.
            return true;
        }

        if (App.Addons.EnabledEntries.Any(entry => entry.HasStreams))
        {
            // A stream addon exists again; explain a future removal afresh.
            _explained = false;
            return true;
        }

        if (_explained)
        {
            openAddonSettings();
            return false;
        }

        if (_dialogOpen || xamlRoot is null)
        {
            // A second click while the explanation is open must not stack
            // dialogs or navigate underneath it.
            return false;
        }

        var hasDisabledAddon = App.Addons.Entries.Any(
            entry => !entry.IsEnabled && entry.HasStreams);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = hasDisabledAddon ? "Your stream addon is turned off" : "Add a stream addon",
            Content = hasDisabledAddon
                ? "Pirsa finds sources through Stremio addons. Turn your stream addon back on in Settings → Addons, then press Play again."
                : "Pirsa ships without a stream addon. It only finds sources through Stremio addons that you add yourself, and most are third-party — add only ones you trust and are allowed to use.",
            PrimaryButtonText = "Open addon settings",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };

        _dialogOpen = true;
        _explained = true;

        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                openAddonSettings();
            }
        }
        catch
        {
            // The page may already be leaving; Settings is still the useful
            // outcome, so do not block it on a dialog failure.
            openAddonSettings();
        }
        finally
        {
            _dialogOpen = false;
        }

        return false;
    }
}

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pirsa.Services;

namespace Pirsa.Helpers;

/// <summary>
/// Turns a <c>stremio://</c> addon link into an installed addon.
///
/// <para>
/// A link can arrive from any web page, so nothing is installed until the user
/// confirms. The addon is checked and added in place, without leaving the
/// current page, so a link that arrives during playback does not close the
/// player. Only the <em>Open addon settings</em> action navigates away.
/// </para>
/// </summary>
public static class AddonLinkPrompt
{
    private static bool _promptOpen;

    /// <summary>Handles one activated link, showing any prompt it needs.</summary>
    public static async Task HandleAsync(string link, XamlRoot? xamlRoot)
    {
        if (xamlRoot is null || _promptOpen)
        {
            return;
        }

        if (!StremioAddonLink.TryParse(link, out var manifestUrl))
        {
            _promptOpen = true;
            try
            {
                await ShowMessageAsync(
                    xamlRoot,
                    "That addon link isn't valid",
                    "Pirsa only opens stremio:// links that point at an http(s) addon manifest.");
            }
            finally
            {
                _promptOpen = false;
            }

            return;
        }

        _promptOpen = true;
        try
        {
            await App.Addons.EnsureLoadedAsync();

            if (App.Addons.GetEntry(manifestUrl) is { } existing)
            {
                await ShowExistingAsync(xamlRoot, existing);
                return;
            }

            var host = new Uri(manifestUrl).Host;
            var confirm = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Add this addon to Pirsa?",
                Content = BuildConfirmContent(host),
                PrimaryButtonText = "Add addon",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await TryShowAsync(confirm) != ContentDialogResult.Primary)
            {
                return;
            }

            await AddAsync(xamlRoot, manifestUrl);
        }
        finally
        {
            _promptOpen = false;
        }
    }

    private static async Task AddAsync(XamlRoot xamlRoot, string manifestUrl)
    {
        AddonEntry? entry = null;
        string? error = null;
        var progress = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Adding addon",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new ProgressRing { Width = 24, Height = 24, IsActive = true },
                    new TextBlock
                    {
                        Text = "Checking the manifest…",
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };

        var progressTask = TryShowAsync(progress);
        try
        {
            entry = await App.Addons.AddAsync(manifestUrl);
        }
        catch (StremioException exception)
        {
            error = exception.Message;
        }
        catch (Exception exception)
        {
            error = $"Couldn't add that addon. {exception.Message}";
        }
        finally
        {
            try
            {
                progress.Hide();
            }
            catch
            {
                // The dialog may never have opened; there is nothing to close.
            }

            await progressTask;
        }

        if (entry is not null)
        {
            var version = string.IsNullOrWhiteSpace(entry.VersionText) ? "" : $" ({entry.VersionText})";
            await ShowMessageAsync(
                xamlRoot,
                "Addon added",
                $"{entry.Name}{version} is now in Settings → Addons. Its catalogs, metadata and sources follow your addon order.");
        }
        else
        {
            await ShowMessageAsync(
                xamlRoot,
                "Couldn't add addon",
                error ?? "The addon didn't return a usable manifest.");
        }
    }

    private static async Task ShowExistingAsync(XamlRoot xamlRoot, AddonEntry existing)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "That addon is already configured",
            Content = existing.IsEnabled
                ? $"{existing.Name} is already in your addon list."
                : $"{existing.Name} is in your addon list but turned off. Turn it on in Settings → Addons.",
            PrimaryButtonText = "Open addon settings",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await TryShowAsync(dialog) == ContentDialogResult.Primary &&
            App.Window is MainWindow window)
        {
            window.OpenAddonSettings();
        }
    }

    private static StackPanel BuildConfirmContent(string host)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = host,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Pirsa will download this addon's manifest. Its catalogs, metadata, subtitles and sources may then appear in the app.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Addons are third-party software. Add only ones from a source you trust and are allowed to use.",
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    private static async Task ShowMessageAsync(XamlRoot xamlRoot, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        await TryShowAsync(dialog);
    }

    /// <summary>Shows a dialog, or reports that it could not be shown.</summary>
    private static async Task<ContentDialogResult> TryShowAsync(ContentDialog dialog)
    {
        try
        {
            return await dialog.ShowAsync();
        }
        catch
        {
            // Another dialog is already open, or the page is going away. The
            // caller treats this like a dismissal instead of crashing.
            return ContentDialogResult.None;
        }
    }
}

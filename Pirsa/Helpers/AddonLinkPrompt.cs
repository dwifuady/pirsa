using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pirsa.Models;
using Pirsa.Services;
using Pirsa.Views;

namespace Pirsa.Helpers;

/// <summary>
/// Turns a <c>stremio://</c> addon link into an installed addon.
///
/// <para>
/// A link can arrive from any web page, so nothing is installed until the user
/// confirms. The confirmation fetches the manifest first and names the addon,
/// its version and what it provides. On success Settings → Addons opens with a
/// notice; while the player is open the outcome is shown in a dialog instead,
/// so a link never closes a running video behind the user's back.
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

        _promptOpen = true;
        try
        {
            if (!StremioAddonLink.TryParse(link, out var manifestUrl))
            {
                await ShowMessageAsync(
                    xamlRoot,
                    "That addon link isn't valid",
                    "Pirsa only opens stremio:// links that point at an http(s) addon manifest.");
                return;
            }

            await App.Addons.EnsureLoadedAsync();

            if (App.Addons.GetEntry(manifestUrl) is { } existing)
            {
                await ReportAsync(
                    xamlRoot,
                    "That addon is already configured",
                    new SettingsMessage(
                        existing.IsEnabled
                            ? $"{existing.Name} is already in your addon list."
                            : $"{existing.Name} is in your addon list but turned off. Turn it on in Settings → Addons.",
                        IsError: false));
                return;
            }

            var manifest = await ConfirmAsync(xamlRoot, manifestUrl);
            if (manifest is null)
            {
                return;
            }

            AddonEntry entry;
            try
            {
                entry = await App.Addons.AddAsync(manifestUrl, manifest);
            }
            catch (StremioException exception)
            {
                await ReportAsync(xamlRoot, "Couldn't add addon", new SettingsMessage(exception.Message, IsError: true));
                return;
            }
            catch (Exception exception)
            {
                await ReportAsync(
                    xamlRoot,
                    "Couldn't add addon",
                    new SettingsMessage($"Couldn't add that addon. {exception.Message}", IsError: true));
                return;
            }

            var version = string.IsNullOrWhiteSpace(entry.VersionText) ? "" : $" ({entry.VersionText})";
            await ReportAsync(
                xamlRoot,
                "Addon installed",
                new SettingsMessage(
                    $"{entry.Name}{version} was added from a stremio:// link.",
                    IsError: false));
        }
        finally
        {
            _promptOpen = false;
        }
    }

    /// <summary>
    /// Fetches the manifest and asks for confirmation. The dialog is opened
    /// immediately so Cancel works while the addon is being checked; the
    /// Install button enables once its details are known.
    /// </summary>
    private static async Task<AddonManifest?> ConfirmAsync(XamlRoot xamlRoot, string manifestUrl)
    {
        var host = new Uri(manifestUrl).Host;

        var progressPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new ProgressRing { Width = 20, Height = 20, IsActive = true },
                new TextBlock
                {
                    Text = "Checking the manifest…",
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        var detailsPanel = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = host,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(progressPanel);
        content.Children.Add(detailsPanel);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Install addon?",
            Content = content,
            PrimaryButtonText = "Install",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        var cancellation = new CancellationTokenSource();
        void CancelFetch(ContentDialog sender, ContentDialogClosedEventArgs args) => cancellation.Cancel();
        dialog.Closed += CancelFetch;

        var showTask = TryStartShow(dialog);
        if (showTask is null)
        {
            dialog.Closed -= CancelFetch;
            cancellation.Dispose();
            return null;
        }

        AddonManifest? manifest = null;
        string? error = null;
        try
        {
            manifest = await new StremioAddonClient(manifestUrl).GetManifestAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancel was pressed while the manifest was loading.
        }
        catch (StremioException exception)
        {
            error = exception.Message;
        }
        catch (Exception exception)
        {
            error = $"Couldn't load that addon's manifest. {exception.Message}";
        }

        if (cancellation.IsCancellationRequested || showTask.IsCompleted)
        {
            await IgnoreResultAsync(showTask);
            dialog.Closed -= CancelFetch;
            cancellation.Dispose();
            return null;
        }

        progressPanel.Visibility = Visibility.Collapsed;
        detailsPanel.Visibility = Visibility.Visible;

        if (manifest is null)
        {
            detailsPanel.Children.Add(new TextBlock
            {
                Text = "Couldn't load this addon's manifest.",
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            detailsPanel.Children.Add(new TextBlock
            {
                Text = error ?? "The addon didn't return a usable manifest.",
                TextWrapping = TextWrapping.Wrap,
            });

            dialog.Title = "Couldn't load that addon";
            dialog.PrimaryButtonText = "";
            dialog.CloseButtonText = "Close";

            await IgnoreResultAsync(showTask);
            dialog.Closed -= CancelFetch;
            cancellation.Dispose();
            return null;
        }

        BuildDetails(detailsPanel, manifestUrl, manifest);
        dialog.IsPrimaryButtonEnabled = true;

        var outcome = ContentDialogResult.None;
        try
        {
            outcome = await showTask;
        }
        catch
        {
            // The dialog could not be shown; treat it as a dismissal.
        }

        dialog.Closed -= CancelFetch;
        cancellation.Dispose();
        return outcome == ContentDialogResult.Primary ? manifest : null;
    }

    /// <summary>Adds the addon's own name, version, and declared capabilities.</summary>
    private static void BuildDetails(StackPanel panel, string manifestUrl, AddonManifest manifest)
    {
        var probe = new AddonEntry(manifestUrl) { Manifest = manifest };

        panel.Children.Add(new TextBlock
        {
            Text = probe.Name,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrWhiteSpace(probe.VersionText))
        {
            panel.Children.Add(new TextBlock
            {
                Text = probe.VersionText,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (!string.IsNullOrWhiteSpace(probe.CapabilitiesText))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Provides: {probe.CapabilitiesText}",
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (!string.IsNullOrWhiteSpace(probe.TypesText))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Types: {probe.TypesText}",
                TextWrapping = TextWrapping.Wrap,
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = "Addons are third-party software. Add only ones from a source you trust and are allowed to use.",
            TextWrapping = TextWrapping.Wrap,
        });
    }

    /// <summary>
    /// Shows the outcome in Settings, or in a dialog when the player is open so
    /// playback is never closed silently.
    /// </summary>
    private static async Task ReportAsync(XamlRoot xamlRoot, string title, SettingsMessage message)
    {
        if (App.Window is MainWindow window && !window.IsPlayerActive)
        {
            window.OpenAddonSettings(message);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = new TextBlock { Text = message.Text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Open addon settings",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await TryShowAsync(dialog) == ContentDialogResult.Primary &&
            App.Window is MainWindow settingsWindow)
        {
            settingsWindow.OpenAddonSettings();
        }
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

    /// <summary>Starts a dialog, or reports that it could not be shown.</summary>
    private static Task<ContentDialogResult>? TryStartShow(ContentDialog dialog)
    {
        try
        {
            return dialog.ShowAsync().AsTask();
        }
        catch
        {
            // Another dialog is already open, or the page is going away.
            return null;
        }
    }

    private static async Task<ContentDialogResult> TryShowAsync(ContentDialog dialog)
    {
        var task = TryStartShow(dialog);
        return task is null ? ContentDialogResult.None : await IgnoreResultAsync(task);
    }

    /// <summary>Awaits a dialog task and treats a failure like a dismissal.</summary>
    private static async Task<ContentDialogResult> IgnoreResultAsync(Task<ContentDialogResult> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            return ContentDialogResult.None;
        }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Pirsa.Helpers;
using Pirsa.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Pirsa.Views;

/// <summary>Settings → About. Version, resources, shortcuts and legal note.</summary>
public sealed partial class AboutSettingsPage : Page
{
    public AboutSettingsPage()
    {
        InitializeComponent();
    }

    public string AppVersion => AppInfo.VersionText;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await App.Settings.EnsureLoadedAsync();

        var size = await Task.Run(() => StorageUsage.SizeOf(LocalPaths.PlayerLog));
        AboutLogSizeText.Text = StorageUsage.Format(size);
    }

    private async void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string url &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(LocalPaths.PlayerLog))
            {
                SetStatus("The player log appears after the first playback.");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = LocalPaths.PlayerLog,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            SetStatus($"Couldn't open the player log. {exception.Message}");
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LocalPaths.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            SetStatus($"Couldn't open the data folder. {exception.Message}");
        }
    }

    private void CopySystemInfo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var engine = string.IsNullOrWhiteSpace(App.Settings.PlaybackEngine)
                ? "Automatic"
                : App.Settings.PlaybackEngine;

            var text = string.Join(
                Environment.NewLine,
                $"Pirsa {AppVersion}",
                $"Windows {Environment.OSVersion.Version}",
                $"Architecture: {RuntimeInformation.ProcessArchitecture}",
                $"Video engine: {engine}");

            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            SetStatus("System information copied.");
        }
        catch (Exception exception)
        {
            SetStatus($"Couldn't copy system information. {exception.Message}");
        }
    }

    private void SetStatus(string message)
    {
        DiagnosticsStatus.Text = message;
        DiagnosticsStatus.Visibility = Visibility.Visible;
    }
}

using Microsoft.Win32;

namespace Pirsa.Services;

/// <summary>
/// Registers Pirsa as the per-user handler for <c>stremio://</c> links.
///
/// <para>
/// An unpackaged build (the Inno Setup installer, the portable ZIP) has no app
/// package to declare the protocol, so Windows learns about it from
/// <c>HKCU\Software\Classes\stremio</c>. The installer writes those keys; this
/// class does the same for portable copies and lets Settings remove them again.
/// A packaged build gets the same behaviour from its manifest and never needs
/// these keys.
/// </para>
/// </summary>
public static class StremioProtocolRegistration
{
    private const string ProtocolKeyPath = @"Software\Classes\" + StremioAddonLink.Scheme;
    private const string CommandKeyPath = ProtocolKeyPath + @"\shell\open\command";
    private const string IconKeyPath = ProtocolKeyPath + @"\DefaultIcon";
    private const string FriendlyName = "URL:Stremio addon link";

    /// <summary>
    /// True when the process has package identity (Store install, or a debug
    /// run through winapp). Uses the same test as <see cref="LocalPaths"/>.
    /// </summary>
    public static bool IsPackaged { get; } = DetectPackageIdentity();

    /// <summary>True when this copy of Pirsa handles <c>stremio://</c> links.</summary>
    public static bool IsRegistered =>
        !IsPackaged && ExecutableMatches(ReadRegisteredCommand());

    /// <summary>
    /// Writes the per-user protocol keys, replacing a handler from another app.
    /// Packaged builds are a no-op because the package manifest owns this.
    /// </summary>
    public static void Register()
    {
        if (IsPackaged)
        {
            return;
        }

        var executable = ExecutablePath();
        if (executable is null)
        {
            throw new InvalidOperationException(
                "Pirsa couldn't work out its own program path to register the link.");
        }

        using var protocol = Registry.CurrentUser.CreateSubKey(ProtocolKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows didn't allow the protocol key to be created.");
        protocol.SetValue(null, FriendlyName);
        protocol.SetValue("URL Protocol", "");

        using (var icon = Registry.CurrentUser.CreateSubKey(IconKeyPath, writable: true))
        {
            icon?.SetValue(null, $"\"{executable}\",0");
        }

        using var command = Registry.CurrentUser.CreateSubKey(CommandKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows didn't allow the link command to be created.");
        command.SetValue(null, $"\"{executable}\" \"%1\"");
    }

    /// <summary>
    /// Removes the per-user keys, but only when they point at this copy, so a
    /// different app's handler is never deleted by mistake. The protocol key
    /// itself is kept: a packaged build of Pirsa may share it, and its
    /// descriptive values are harmless on their own.
    /// </summary>
    public static void Unregister()
    {
        if (IsPackaged || !IsRegistered)
        {
            return;
        }

        Registry.CurrentUser.DeleteSubKeyTree(CommandKeyPath, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(IconKeyPath, throwOnMissingSubKey: false);

        // Clean up the empty parents we created, but never the protocol key.
        DeleteEmptyKey(ProtocolKeyPath + @"\shell\open");
        DeleteEmptyKey(ProtocolKeyPath + @"\shell");
    }

    /// <summary>The program path the protocol should launch.</summary>
    private static string? ExecutablePath()
    {
        // Use the apphost next to the build/install first: Environment.ProcessPath
        // can be dotnet.exe when the app was started through a host.
        var appHost = Path.Combine(AppContext.BaseDirectory, "Pirsa.exe");
        if (File.Exists(appHost))
        {
            return appHost;
        }

        return Environment.ProcessPath is { Length: > 0 } processPath &&
               !Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? processPath
            : null;
    }

    private static string? ReadRegisteredCommand()
    {
        try
        {
            using var command = Registry.CurrentUser.OpenSubKey(CommandKeyPath);
            return command?.GetValue(null) as string;
        }
        catch
        {
            // A malformed or inaccessible key simply means "not registered".
            return null;
        }
    }

    /// <summary>Deletes a key only when it is empty.</summary>
    private static void DeleteEmptyKey(string path)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            if (key is not null && key.SubKeyCount == 0 && key.ValueCount == 0)
            {
                Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
            }
        }
        catch
        {
            // Leaving an empty key behind is harmless; never fail the toggle.
        }
    }

    /// <summary>True when a shell command runs this copy of Pirsa.</summary>
    private static bool ExecutableMatches(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var registered = FirstQuotedValue(command) ?? command.Trim();
        var current = ExecutablePath();
        if (current is null)
        {
            return false;
        }

        try
        {
            return Path.GetFullPath(registered)
                .Equals(Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? FirstQuotedValue(string value)
    {
        var start = value.IndexOf('"');
        if (start < 0)
        {
            return null;
        }

        var end = value.IndexOf('"', start + 1);
        return end < 0 ? null : value[(start + 1)..end];
    }

    private static bool DetectPackageIdentity()
    {
        try
        {
            _ = Windows.ApplicationModel.Package.Current;
            return true;
        }
        catch
        {
            return false;
        }
    }
}

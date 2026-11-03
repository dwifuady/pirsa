using System.Reflection;

namespace Pirsa.Helpers;

/// <summary>
/// Build identity shown in the UI: the title bar and Settings → About.
/// Read from assembly metadata, so it works packed or unpacked and matches
/// whatever the release workflow stamped on the build.
/// </summary>
public static class AppInfo
{
    /// <summary>The release version without build metadata, e.g. "0.1.1".</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>True when <see cref="Version"/> is known.</summary>
    public static bool HasVersion => Version.Length > 0;

    /// <summary>Display form for the UI, e.g. "v0.1.1" — empty if unknown.</summary>
    public static string VersionText => HasVersion ? $"v{Version}" : string.Empty;

    private static string ReadVersion()
    {
        var informational = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Release builds append "+<commit sha>"; the UI only wants the number.
            var buildMetadata = informational.IndexOf('+');
            return buildMetadata > 0 ? informational[..buildMetadata] : informational;
        }

        // Fall back to the assembly version (e.g. "0.1.1.0") when no
        // informational version was stamped.
        var assemblyVersion = typeof(AppInfo).Assembly.GetName().Version;
        return assemblyVersion is null
            ? string.Empty
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
    }
}

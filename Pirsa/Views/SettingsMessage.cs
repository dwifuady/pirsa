namespace Pirsa.Views;

/// <summary>
/// A notice or error to show once Settings opens, used when an addon link
/// installs an addon, or fails to, without closing the current page first.
/// </summary>
public sealed record SettingsMessage(string Text, bool IsError);

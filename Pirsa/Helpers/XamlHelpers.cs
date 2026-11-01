using Microsoft.UI.Xaml;

namespace Pirsa.Helpers;

/// <summary>
/// Small static helpers used by x:Bind so pages don't need value converters.
/// </summary>
public static class XamlHelpers
{
    public static Visibility Visible(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;
}

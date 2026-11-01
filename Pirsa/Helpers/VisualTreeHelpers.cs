using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Pirsa.Helpers;

/// <summary>Small visual-tree lookups shared by poster surfaces.</summary>
public static class VisualTreeHelpers
{
    /// <summary>Depth-first search for the first descendant of <typeparamref name="T"/>.</summary>
    public static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}

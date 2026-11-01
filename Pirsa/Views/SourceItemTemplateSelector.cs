using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pirsa.ViewModels;

namespace Pirsa.Views;

/// <summary>
/// Chooses the addon group header template or the source row template for the
/// flattened source picker.
/// </summary>
public sealed partial class SourceItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }

    public DataTemplate? SourceTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is SourceListItem { IsHeader: true } ? HeaderTemplate : SourceTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}

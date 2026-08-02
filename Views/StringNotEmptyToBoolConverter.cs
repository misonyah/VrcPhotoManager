using System.Globalization;
using System.Windows.Data;

namespace VrcPhotoManager.Views;

/// <summary>
/// Companion to NullToVisibilityConverter, but for ToolTipService.IsEnabled rather than
/// Visibility. Needed because binding ToolTip to an explicit &lt;ToolTip&gt; element (rather
/// than the plain `ToolTip="{Binding ...}"` attribute form) makes the ToolTip property itself
/// always non-null - WPF's built-in "don't show a tooltip when the bound value is null"
/// suppression only fires off the ToolTip property being null, not its Content, so a
/// TextBlock.ToolTip set to `&lt;ToolTip Content="{Binding NoteTooltip}"&gt;` would otherwise
/// pop an empty bubble whenever NoteTooltip is null. Binding ToolTipService.IsEnabled through
/// this converter suppresses the tooltip from opening at all in that case.
/// </summary>
public class StringNotEmptyToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        !string.IsNullOrEmpty(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

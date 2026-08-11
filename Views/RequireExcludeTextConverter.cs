using System.Globalization;
using System.Windows.Data;

namespace VrcPhotoManager.Views;

/// <summary>Labels FilterWindow's per-row require/exclude ToggleButton - PlayerFilterRow.Exclude
/// (bool) to "Requires"/"Excludes" text.</summary>
public class RequireExcludeTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Excludes" : "Requires";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using VrcPhotoManager.Models;

namespace VrcPhotoManager.Views;

/// <summary>Shows an element only when RemoteStatus is one of a comma-separated set of names
/// passed via ConverterParameter (e.g. "Uploading,Failed") - lets the thumbnail badge split
/// into separate elements per status group (a cyan cloud glyph for Uploaded, a colored text
/// pill for the transient Uploading/Failed states, nothing at all for the common NotUploaded
/// case) without a dedicated converter class per group.</summary>
public class RemoteStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not RemoteStatus status || parameter is not string allowedNames) return Visibility.Collapsed;
        var allowed = allowedNames.Split(',', StringSplitOptions.TrimEntries);
        return allowed.Contains(status.ToString()) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

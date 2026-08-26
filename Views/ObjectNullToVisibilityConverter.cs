using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VrcPhotoManager.Views;

/// <summary>Like NullToVisibilityConverter, but for any bound object (BitmapImage, records,
/// etc.) instead of specifically strings - that converter's `value as string` cast silently
/// returns null (and therefore always Collapsed) for anything that isn't actually a string,
/// found via a real report of a badge that could never show regardless of its bound value.</summary>
public class ObjectNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

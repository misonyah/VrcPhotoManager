using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using VrcPhotoManager.Models;

namespace VrcPhotoManager.Views;

public class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RemoteStatus.Uploaded => Brushes.SeaGreen,
        RemoteStatus.Uploading => Brushes.DarkOrange,
        RemoteStatus.Failed => Brushes.Firebrick,
        _ => Brushes.Gray,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

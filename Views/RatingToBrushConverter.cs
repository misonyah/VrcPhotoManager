using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace VrcdnManager.Views;

public class RatingToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string) switch
    {
        "general" => Brushes.SeaGreen,
        "sensitive" => Brushes.Goldenrod,
        "questionable" => Brushes.DarkOrange,
        "explicit" => Brushes.DeepPink, // most lewd - stands out deliberately
        _ => Brushes.Gray, // not yet classified
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

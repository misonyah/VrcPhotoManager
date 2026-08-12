using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace VrcPhotoManager.Views;

/// <summary>Shows the crop-line preview overlay only when the photo is selected, an upload crop
/// ratio is active (not "Original"), and the thumbnail has actually loaded (needed to compute
/// where the lines go). Values: [0] Selected (bool), [1] MainViewModel.EffectiveCropAspectRatio
/// (double?), [2] Thumbnail (BitmapImage?).</summary>
public class CropOverlayVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool selected = values.Length > 0 && values[0] is true;
        bool hasRatio = values.Length > 1 && values[1] is double;
        bool hasThumbnail = values.Length > 2 && values[2] is BitmapImage;
        return selected && hasRatio && hasThumbnail ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Computes the Margin (inside the square thumbnail cell) that draws the crop-line
/// preview at the same centered-crop rectangle ThumbnailService.PrepareForUploadAsync would
/// actually cut - so what's inside the lines is what gets uploaded. Has to account for the
/// Image's own Stretch="Uniform" letterboxing within the square cell (the displayed image
/// doesn't fill the cell unless it's already square), not just the target ratio on its own.
/// Values: [0] Thumbnail (BitmapImage?), [1] container size (double, the square cell's
/// Width/Height), [2] target aspect ratio (double?, Width/Height).</summary>
public class CropOverlayMarginConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3
            || values[0] is not BitmapImage thumbnail
            || values[1] is not double containerSize
            || values[2] is not double targetRatio
            || thumbnail.PixelWidth <= 0 || thumbnail.PixelHeight <= 0 || containerSize <= 0)
        {
            return new Thickness(0);
        }

        // Matches Image's Stretch="Uniform": scale by whichever axis is more constraining.
        double scale = Math.Min(containerSize / thumbnail.PixelWidth, containerSize / thumbnail.PixelHeight);

        double dispWidth = thumbnail.PixelWidth * scale;
        double dispHeight = thumbnail.PixelHeight * scale;
        double dispLeft = (containerSize - dispWidth) / 2;
        double dispTop = (containerSize - dispHeight) / 2;

        double cropWidth, cropHeight;
        if (dispWidth / dispHeight > targetRatio)
        {
            cropHeight = dispHeight;
            cropWidth = dispHeight * targetRatio;
        }
        else
        {
            cropWidth = dispWidth;
            cropHeight = dispWidth / targetRatio;
        }

        double left = dispLeft + (dispWidth - cropWidth) / 2;
        double top = dispTop + (dispHeight - cropHeight) / 2;
        double right = containerSize - (left + cropWidth);
        double bottom = containerSize - (top + cropHeight);
        return new Thickness(left, top, right, bottom);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

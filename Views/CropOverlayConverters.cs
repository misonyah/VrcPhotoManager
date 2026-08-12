using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using VrcPhotoManager.Models;
using VrcPhotoManager.Services;

namespace VrcPhotoManager.Views;

/// <summary>Resolves which crop ratio the preview overlay should show for a given photo - the
/// actual ratio it was uploaded as (parsed from its own UploadCropMode) once it's Uploaded,
/// rather than whatever preset happens to be selected in the dropdown right now. The two can
/// disagree (you can select a different preset after uploading, or a re-sync backfilled a
/// crop from a resolution the dropdown never had selected), and the preview on an already-
/// uploaded photo should show what's really live on VRCDN, not what a fresh upload would do.
/// Not-yet-uploaded photos still preview the dropdown's ratio, since that's what a future
/// upload would actually apply.</summary>
internal static class CropOverlayRatioResolver
{
    public static double? Resolve(RemoteStatus status, string? uploadCropMode, double? dropdownRatio) =>
        status == RemoteStatus.Uploaded
            ? (uploadCropMode is null ? null : CropRatioLabels.ParseRatio(uploadCropMode))
            : dropdownRatio;

    /// <summary>The crop nudge (see Photo.CropOffsetX) only ever applies to a not-yet-uploaded
    /// photo's preview - an already-uploaded photo's crop position isn't tracked (only its
    /// ratio is, via UploadCropMode), so its preview always shows centered.</summary>
    public static (double X, double Y) ResolveOffset(RemoteStatus status, double offsetX, double offsetY) =>
        status == RemoteStatus.Uploaded ? (0, 0) : (offsetX, offsetY);
}

/// <summary>Shows the crop-line preview overlay when selected (preview of what a future upload
/// would cut), or on hover ONLY for a photo that's already Uploaded (the real crop it's live
/// with) - hovering a not-yet-uploaded, unselected photo shouldn't show a crop guess nobody
/// asked for. Also requires the resolved ratio (see CropOverlayRatioResolver) to be set and the
/// thumbnail to have actually loaded (needed to compute where the lines go). Values:
/// [0] Selected (bool), [1] IsMouseOver of the photo's outer Border (bool), [2] RemoteStatus,
/// [3] UploadCropMode (string?), [4] MainViewModel.EffectiveCropAspectRatio - the dropdown's
/// ratio (double?), [5] Thumbnail (BitmapImage?).</summary>
public class CropOverlayVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 6
            || values[0] is not bool selected
            || values[1] is not bool isMouseOver
            || values[2] is not RemoteStatus status
            || values[5] is not BitmapImage)
        {
            return Visibility.Collapsed;
        }

        string? uploadCropMode = values[3] as string;
        double? dropdownRatio = values[4] as double?;
        double? ratio = CropOverlayRatioResolver.Resolve(status, uploadCropMode, dropdownRatio);
        bool show = selected || (isMouseOver && status == RemoteStatus.Uploaded);
        return show && ratio is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Computes the Margin (inside the square thumbnail cell) that draws the crop-line
/// preview at the same centered-crop rectangle ThumbnailService.PrepareForUploadAsync would
/// actually cut (or, for an already-uploaded photo, the crop it was actually uploaded with - see
/// CropOverlayRatioResolver) - so what's inside the lines is what's really there. Has to account
/// for the Image's own Stretch="Uniform" letterboxing within the square cell (the displayed image
/// doesn't fill the cell unless it's already square), not just the target ratio on its own.
/// Values: [0] Thumbnail (BitmapImage?), [1] container size (double - the Grid's own
/// ActualWidth, NOT the outer Border's bound Width, since selection changes that Border's
/// BorderThickness and shrinks the actual content area), [2] RemoteStatus, [3] UploadCropMode
/// (string?), [4] MainViewModel's dropdown ratio (double?), [5] CropOffsetX (double),
/// [6] CropOffsetY (double).</summary>
public class CropOverlayMarginConverter : IMultiValueConverter
{
    /// <summary>Nudges the whole overlay up by 1px versus the mathematically-centered position -
    /// a consistent, reported-as-visible 1px vertical bias against the actual rendered photo
    /// (plausibly WPF's own sub-pixel text/image baseline rounding), not something the
    /// centered-crop math itself gets wrong.</summary>
    private const double VerticalNudge = -1;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 7
            || values[0] is not BitmapImage thumbnail
            || values[1] is not double containerSize
            || values[2] is not RemoteStatus status
            || values[5] is not double offsetX
            || values[6] is not double offsetY
            || thumbnail.PixelWidth <= 0 || thumbnail.PixelHeight <= 0 || containerSize <= 0)
        {
            return new Thickness(0);
        }

        string? uploadCropMode = values[3] as string;
        double? dropdownRatio = values[4] as double?;
        double? targetRatio = CropOverlayRatioResolver.Resolve(status, uploadCropMode, dropdownRatio);
        if (targetRatio is not double ratio) return new Thickness(0);
        (double offX, double offY) = CropOverlayRatioResolver.ResolveOffset(status, offsetX, offsetY);

        // Matches Image's Stretch="Uniform": scale by whichever axis is more constraining.
        double scale = Math.Min(containerSize / thumbnail.PixelWidth, containerSize / thumbnail.PixelHeight);

        double dispWidth = thumbnail.PixelWidth * scale;
        double dispHeight = thumbnail.PixelHeight * scale;
        double dispLeft = (containerSize - dispWidth) / 2;
        double dispTop = (containerSize - dispHeight) / 2;

        double cropWidth, cropHeight;
        if (dispWidth / dispHeight > ratio)
        {
            cropHeight = dispHeight;
            cropWidth = dispHeight * ratio;
        }
        else
        {
            cropWidth = dispWidth;
            cropHeight = dispWidth / ratio;
        }

        // offX/offY (-1..1) position the crop within its available slack on each axis instead
        // of always centering it (0 = centered) - matches ThumbnailService.PrepareForUploadAsync's
        // same-shaped formula, so the preview lines land exactly where the real upload would crop.
        double slackX = dispWidth - cropWidth;
        double slackY = dispHeight - cropHeight;
        double left = dispLeft + slackX / 2 * (1 + offX);
        double top = dispTop + slackY / 2 * (1 + offY) + VerticalNudge;
        double right = containerSize - (left + cropWidth);
        double bottom = containerSize - (top + cropHeight);
        return new Thickness(left, top, right, bottom);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

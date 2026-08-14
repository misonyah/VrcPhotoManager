using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using VrcPhotoManager.Models;
using VrcPhotoManager.Services;
using VrcPhotoManager.ViewModels;

namespace VrcPhotoManager.Views;

/// <summary>Resolves which crop ratio the preview overlay should show for a given photo. There's
/// no batch-wide default crop anymore - only a per-photo CropRatioOverride (set via [ / ] while
/// hovering, see Photo.CropRatioOverride) or, once Uploaded, the ratio it was actually uploaded
/// as (parsed from UploadCropMode).</summary>
internal static class CropOverlayRatioResolver
{
    /// <summary>cropRatioOverride is a preset Name (or null - "no crop"), parsed the same way
    /// UploadCropMode is. "Original (no crop)" isn't in CropRatioLabels.KnownRatios and isn't a
    /// "Custom ..." shape either, so ParseRatio naturally returns null for it - correctly showing
    /// no crop lines for a photo overridden to that preset (same outcome as no override at all).
    /// cropRatioOverride wins whenever it's set (even on an Uploaded photo) so a tentative
    /// browsed-but-not-yet-reuploaded candidate (see PhotoViewModel.HasPendingCropEdit) previews
    /// correctly; uploadCropMode is only the fallback for Uploaded photos with no override at all
    /// (e.g. backfilled by SyncRemoteMatches) - it reflects what's really live on VRCDN.</summary>
    public static double? Resolve(RemoteStatus status, string? uploadCropMode, string? cropRatioOverride)
    {
        string? effective = cropRatioOverride ?? (status == RemoteStatus.Uploaded ? uploadCropMode : null);
        return effective is null ? null : CropRatioLabels.ParseRatio(effective);
    }
}

/// <summary>Shows the crop-line preview overlay when selected (preview of what a future upload
/// would cut) or on hover (same preview, or the real uploaded crop once Uploaded) - hover has to
/// show it for a not-yet-uploaded, unselected photo too now that arrow keys nudge whichever
/// photo is hovered (MainWindow's PreviewKeyDown -> PhotoViewModel.NudgeCropOffset): an earlier
/// version only showed hover-preview for already-Uploaded photos, so nudging an unselected photo
/// actually worked but gave no visible feedback at all - confusing enough that it read as
/// "cursor keys don't work until I deselect the other photo" (the other photo's own, unrelated,
/// still-visible-because-Selected overlay was the only thing on screen). Also requires the
/// resolved ratio (see CropOverlayRatioResolver) to be set and the thumbnail to have actually
/// loaded (needed to compute where the lines go). Values: [0] Selected (bool), [1] IsMouseOver
/// of the photo's outer Border (bool), [2] RemoteStatus, [3] UploadCropMode (string?),
/// [4] CropRatioOverride (string?), [5] Thumbnail (BitmapImage?).</summary>
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
        string? cropRatioOverride = values[4] as string;
        double? ratio = CropOverlayRatioResolver.Resolve(status, uploadCropMode, cropRatioOverride);
        bool show = selected || isMouseOver;
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
/// (string?), [4] CropRatioOverride (string?), [5] CropOffsetX (double), [6] CropOffsetY
/// (double).</summary>
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
        string? cropRatioOverride = values[4] as string;
        double? targetRatio = CropOverlayRatioResolver.Resolve(status, uploadCropMode, cropRatioOverride);
        if (targetRatio is not double ratio) return new Thickness(0);

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

        // offsetX/Y (-1..1) position the crop within its available slack on each axis instead of
        // always centering it (0 = centered) - matches ThumbnailService.PrepareForUploadAsync's
        // same-shaped formula, so the preview lines land exactly where the real upload would
        // crop. Used regardless of RemoteStatus - CropOffsetX/Y are no longer reset on upload
        // (see UploadSelectedAsync), so they keep meaning "where the crop actually is" even for
        // an already-Uploaded photo.
        double slackX = dispWidth - cropWidth;
        double slackY = dispHeight - cropHeight;
        double left = dispLeft + slackX / 2 * (1 + offsetX);
        double top = dispTop + slackY / 2 * (1 + offsetY) + VerticalNudge;
        double right = containerSize - (left + cropWidth);
        double bottom = containerSize - (top + cropHeight);
        return new Thickness(left, top, right, bottom);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows a small text badge naming the crop setting that's CURRENTLY actually in effect
/// for a not-yet-uploaded photo - this photo's own CropRatioOverride if [ / ] has set one,
/// otherwise "Original (no crop)" (there's no batch-wide default anymore, so no override really
/// does mean no crop). Exists because "no override" and "override explicitly set to Original (no
/// crop)" would otherwise look identical in the crop-line overlay itself (no lines either way),
/// making it impossible to tell from the overlay alone whether cycling with [ / ] actually did
/// anything. Shown at the same corner an already-Uploaded photo's real "Uploaded as" badge
/// occupies (they're mutually exclusive by RemoteStatus, so never both at once).</summary>
public class PendingCropLabelVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3
            || values[0] is not bool selected
            || values[1] is not bool isMouseOver
            || values[2] is not RemoteStatus status)
        {
            return Visibility.Collapsed;
        }
        return (selected || isMouseOver) && status != RemoteStatus.Uploaded ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>The short label text for PendingCropLabelVisibilityConverter's badge - see its own
/// doc comment. Value: CropRatioOverride (string?) - null displays as "Original (no crop)"'s
/// short form, matching what actually happens on upload with no override set.</summary>
public class EffectiveCropLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        CropRatioLabels.ShortLabel(value as string ?? MainViewModel.UploadCropModeOriginal);

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

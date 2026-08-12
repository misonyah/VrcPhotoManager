namespace VrcPhotoManager.Services;

/// <summary>Shared ratio/label table for the upload-crop feature - kept out of MainViewModel so
/// PhotoRepository's retroactive backfill (labeling an already-uploaded photo's crop from its
/// synced remote resolution, with no ViewModels dependency) and the crop-line preview converters
/// can both use it without duplicating the preset ratios.</summary>
public static class CropRatioLabels
{
    public static readonly (string Name, double Ratio)[] KnownRatios =
    [
        ("1:1 (Square)", 1.0),
        ("3:4 (Portrait)", 3.0 / 4.0),
        ("4:3 (Landscape)", 4.0 / 3.0),
        ("9:16 (Portrait)", 9.0 / 16.0),
        ("16:9 (Landscape)", 16.0 / 9.0),
    ];

    private const double Tolerance = 0.01;

    /// <summary>Best-effort label for an arbitrary uploaded resolution - matches a known preset
    /// ratio within a small tolerance (server-side JPEG re-encoding can round dimensions by a
    /// pixel or two), falling back to "Custom W:H" reduced to lowest terms.</summary>
    public static string ForResolution(int width, int height)
    {
        double ratio = (double)width / height;
        foreach (var (name, knownRatio) in KnownRatios)
        {
            if (Math.Abs(ratio - knownRatio) < Tolerance) return name;
        }

        int divisor = Gcd(width, height);
        return $"Custom {width / divisor}:{height / divisor}";
    }

    /// <summary>Parses a label produced by ForResolution - or MainViewModel's own preset names /
    /// "Custom W:H" labels, which share the same shapes - back into a Width/Height ratio.</summary>
    public static double? ParseRatio(string label)
    {
        foreach (var (name, knownRatio) in KnownRatios)
        {
            if (label == name) return knownRatio;
        }

        const string prefix = "Custom ";
        if (label.StartsWith(prefix, StringComparison.Ordinal))
        {
            var parts = label[prefix.Length..].Split(':');
            if (parts.Length == 2
                && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out double w)
                && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out double h)
                && h != 0)
            {
                return w / h;
            }
        }
        return null;
    }

    /// <summary>Trims a full preset/UploadCropMode label down to just the ratio for a small
    /// thumbnail badge, e.g. "4:3" out of "4:3 (Landscape)" - the full label is still shown via
    /// the badge's own ToolTip. Shared so PhotoViewModel.UploadCropModeShort and the per-photo
    /// pending-crop badge (CropOverlayConverters.EffectiveCropLabelConverter) display the same
    /// shortening for the same label shapes.</summary>
    public static string ShortLabel(string fullLabel)
    {
        int parenIndex = fullLabel.IndexOf(" (", StringComparison.Ordinal);
        return parenIndex > 0 ? fullLabel[..parenIndex] : fullLabel;
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}

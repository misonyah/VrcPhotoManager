using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace VrcPhotoManager.Services;

/// <summary>
/// Generates small JPEG thumbnails (as bytes, stored as a BLOB in the photos db rather
/// than loose files - SQLite's own docs note blobs under ~100KB read faster than separate
/// files for this exact size/count profile, and it avoids managing thousands of loose
/// cache files). Uses WPF's built-in imaging (BitmapImage/JpegBitmapEncoder) rather than a
/// third-party library - SixLabors.ImageSharp 4.0+ requires a paid commercial license.
/// </summary>
public class ThumbnailService
{
    private const int ThumbnailMaxSide = 512;

    /// <summary>
    /// VRChat's Udon image loader hard-caps at 2048x2048 (creators.vrchat.com/worlds/udon/
    /// image-loading) - matches what the original prepare_upload_batch.py did before any
    /// photo goes to VRCDN. Re-encodes as JPEG q92 regardless of source format. Public so
    /// SettingsWindow's crop-presets reference list can compute the same example max
    /// resolutions from one source of truth instead of a second hardcoded 2048.
    /// </summary>
    public const int UploadMaxSide = 2048;

    public async Task<byte[]> GenerateThumbnailAsync(string localPath, CancellationToken ct = default) =>
        (await ResizeAsync(localPath, ThumbnailMaxSide, quality: 85, cropAspectRatio: null, cropOffsetX: 0, cropOffsetY: 0, ct)).Bytes;

    /// <summary>cropAspectRatio is Width/Height (e.g. 0.75 for a 3:4 portrait crop) - null
    /// uploads the photo at its original aspect ratio, unchanged from before crop-on-upload
    /// existed. cropOffsetX/Y are -1..1 fractions of the available slack on each axis (0 =
    /// centered - see Photo.CropOffsetX's doc comment for how they get set). Returns the final
    /// pixel dimensions alongside the encoded bytes so the caller can encode them into the
    /// uploaded filename (see MainViewModel.UploadSelectedAsync) - only meaningful to show for
    /// a cropped upload, since an uncropped one already keeps its original filename
    /// verbatim.</summary>
    public Task<(byte[] Bytes, int Width, int Height)> PrepareForUploadAsync(
        string localPath, double? cropAspectRatio, double cropOffsetX = 0, double cropOffsetY = 0,
        CancellationToken ct = default) =>
        ResizeAsync(localPath, UploadMaxSide, quality: 92, cropAspectRatio, cropOffsetX, cropOffsetY, ct);

    private static Task<(byte[] Bytes, int Width, int Height)> ResizeAsync(
        string localPath, int maxSide, int quality, double? cropAspectRatio,
        double cropOffsetX, double cropOffsetY, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            // Peek dimensions without a full decode first, so we constrain whichever axis
            // is larger (matches the original Python script's "fit within maxSide, never
            // upscale" logic) instead of always constraining width, which would distort or
            // upscale portrait images.
            var probe = BitmapDecoder.Create(new Uri(localPath), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            int origWidth = probe.Frames[0].PixelWidth;
            int origHeight = probe.Frames[0].PixelHeight;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (origWidth >= origHeight && origWidth > maxSide)
            {
                bmp.DecodePixelWidth = maxSide;
            }
            else if (origHeight > origWidth && origHeight > maxSide)
            {
                bmp.DecodePixelHeight = maxSide;
            }
            // else already within bounds - decode at native size, don't upscale
            bmp.UriSource = new Uri(localPath);
            bmp.EndInit();
            bmp.Freeze();

            BitmapSource final = bmp;
            if (cropAspectRatio is double targetRatio)
            {
                int w = bmp.PixelWidth, h = bmp.PixelHeight;
                double currentRatio = (double)w / h;
                bool cropSides = currentRatio > targetRatio;
                int cropWidth = cropSides ? Math.Max(1, (int)Math.Round(h * targetRatio)) : w;
                int cropHeight = cropSides ? h : Math.Max(1, (int)Math.Round(w / targetRatio));

                // cropOffsetX/Y (-1..1) position the crop within its available slack on each
                // axis instead of always centering it (0 = centered, the previous behavior) -
                // -1/+1 pin it to one edge. A no-op on whichever axis isn't actually cropped
                // (its slack is 0 there).
                int x = cropSides
                    ? Math.Clamp((int)Math.Round((w - cropWidth) / 2.0 * (1 + cropOffsetX)), 0, w - cropWidth)
                    : 0;
                int y = cropSides
                    ? 0
                    : Math.Clamp((int)Math.Round((h - cropHeight) / 2.0 * (1 + cropOffsetY)), 0, h - cropHeight);
                final = new CroppedBitmap(bmp, new Int32Rect(x, y, cropWidth, cropHeight));
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(final));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            return (stream.ToArray(), final.PixelWidth, final.PixelHeight);
        }, ct);
    }
}

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
    /// photo goes to VRCDN. Re-encodes as JPEG q92 regardless of source format.
    /// </summary>
    private const int UploadMaxSide = 2048;

    public async Task<byte[]> GenerateThumbnailAsync(string localPath, CancellationToken ct = default) =>
        (await ResizeAsync(localPath, ThumbnailMaxSide, quality: 85, cropAspectRatio: null, ct)).Bytes;

    /// <summary>cropAspectRatio is Width/Height (e.g. 0.75 for a 3:4 portrait crop) - null
    /// uploads the photo at its original aspect ratio, unchanged from before crop-on-upload
    /// existed. Returns the final pixel dimensions alongside the encoded bytes so the caller
    /// can encode them into the uploaded filename (see MainViewModel.UploadSelectedAsync) -
    /// only meaningful to show for a cropped upload, since an uncropped one already keeps its
    /// original filename verbatim.</summary>
    public Task<(byte[] Bytes, int Width, int Height)> PrepareForUploadAsync(
        string localPath, double? cropAspectRatio, CancellationToken ct = default) =>
        ResizeAsync(localPath, UploadMaxSide, quality: 92, cropAspectRatio, ct);

    private static Task<(byte[] Bytes, int Width, int Height)> ResizeAsync(
        string localPath, int maxSide, int quality, double? cropAspectRatio, CancellationToken ct)
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
                int cropWidth, cropHeight, x, y;
                if (currentRatio > targetRatio)
                {
                    // Wider than the target ratio - crop the sides, keep full height centered.
                    cropHeight = h;
                    cropWidth = Math.Max(1, (int)Math.Round(h * targetRatio));
                    x = (w - cropWidth) / 2;
                    y = 0;
                }
                else
                {
                    // Taller than the target ratio - crop top/bottom, keep full width centered.
                    cropWidth = w;
                    cropHeight = Math.Max(1, (int)Math.Round(w / targetRatio));
                    x = 0;
                    y = (h - cropHeight) / 2;
                }
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

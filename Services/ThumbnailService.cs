using System.IO;
using System.Windows.Media.Imaging;

namespace VrcdnManager.Services;

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

    public Task<byte[]> GenerateThumbnailAsync(string localPath, CancellationToken ct = default) =>
        ResizeAsync(localPath, ThumbnailMaxSide, quality: 85, ct);

    public Task<byte[]> PrepareForUploadAsync(string localPath, CancellationToken ct = default) =>
        ResizeAsync(localPath, UploadMaxSide, quality: 92, ct);

    private static Task<byte[]> ResizeAsync(string localPath, int maxSide, int quality, CancellationToken ct)
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

            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(bmp));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }, ct);
    }
}

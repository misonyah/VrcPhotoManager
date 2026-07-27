using System.IO;
using System.Windows.Media.Imaging;

namespace VrcdnManager.Services;

/// <summary>
/// Generates small local JPEG thumbnails so the grid can scroll through thousands of
/// photos without decoding full-res originals (some of which are 4320x7680+). Uses WPF's
/// built-in imaging (BitmapImage/JpegBitmapEncoder) rather than a third-party library -
/// SixLabors.ImageSharp 4.0+ requires a paid commercial license as of this writing.
/// </summary>
public class ThumbnailService
{
    private const int ThumbnailMaxSide = 512;
    private readonly string _cacheDir;

    public ThumbnailService(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
    }

    public string GetThumbnailPath(string localPath)
    {
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(localPath)))[..16];
        return Path.Combine(_cacheDir, $"{hash}.jpg");
    }

    public Task<string> EnsureThumbnailAsync(string localPath, CancellationToken ct = default)
    {
        string thumbPath = GetThumbnailPath(localPath);
        if (File.Exists(thumbPath)) return Task.FromResult(thumbPath);

        return Task.Run(() =>
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            // Decode directly at (roughly) thumbnail resolution - avoids fully decoding a
            // 4320x7680 original just to shrink it afterward.
            bmp.DecodePixelWidth = ThumbnailMaxSide;
            bmp.UriSource = new Uri(localPath);
            bmp.EndInit();
            bmp.Freeze();

            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(bmp));

            using (var stream = new FileStream(thumbPath, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(stream);
            }

            return thumbPath;
        }, ct);
    }
}

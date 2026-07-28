using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VrcPhotoManager.Services;

/// <summary>
/// VRChat's in-game "Print" feature pads photos to 2048x1440 with a white border around the
/// actual 1920x1080 content. Ports the exact detection/crop logic from the original
/// crop_print.py: check the border pixels just outside the inner image are pure white before
/// cropping (so a real 2048x1440 photo that ISN'T print-padded doesn't get miscropped), then
/// crop to (64, 69)-(1984, 1149). Originals are never touched - saves a new file.
/// </summary>
public static class CropPrintService
{
    private const int ExpectedWidth = 2048;
    private const int ExpectedHeight = 1440;
    private const int Left = 64, Top = 69, Right = 1984, Bottom = 1149;

    public static bool LooksLikePrintFormat(int? width, int? height) =>
        width == ExpectedWidth && height == ExpectedHeight;

    /// <summary>Checks the 1px border strips just outside the inner image are pure white.</summary>
    public static bool HasWhiteBorder(string path)
    {
        var frame = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);

        int width = converted.PixelWidth, height = converted.PixelHeight;
        if (width != ExpectedWidth || height != ExpectedHeight) return false;

        int stride = width * 3;
        byte[] pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        bool IsWhite(int x, int y)
        {
            int i = y * stride + x * 3;
            return pixels[i] == 255 && pixels[i + 1] == 255 && pixels[i + 2] == 255;
        }

        for (int x = Left; x < Right; x++)
        {
            if (!IsWhite(x, 0) || !IsWhite(x, height - 1)) return false;
        }
        for (int y = Top; y < Bottom; y++)
        {
            if (!IsWhite(0, y) || !IsWhite(width - 1, y)) return false;
        }
        return true;
    }

    /// <summary>Crops the print border off and saves a new file next to the original (e.g.
    /// "..._2048x1440.png" -> "..._1920x1080.png"). Returns the new file's path.</summary>
    public static string CropAndSave(string path)
    {
        var frame = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var cropped = new CroppedBitmap(frame, new Int32Rect(Left, Top, Right - Left, Bottom - Top));

        string newPath = path.Contains("2048x1440")
            ? path.Replace("2048x1440", "1920x1080")
            : Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}_1920x1080{Path.GetExtension(path)}");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cropped));
        using var stream = new FileStream(newPath, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);

        return newPath;
    }
}

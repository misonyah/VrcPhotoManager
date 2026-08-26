using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace VrcPhotoManager.Services;

/// <summary>Process-lifetime, in-memory cache of decoded Discord guild icon bitmaps, keyed by
/// icon URL - every photo from the same Discord library shares one guild, so this avoids
/// re-downloading/re-decoding the same small icon once per thumbnail. Deliberately a static
/// shared cache rather than threaded through every PhotoViewModel construction site: the icon
/// is read-only, small, and looked up purely by URL, so there's no meaningful downside to a
/// single process-wide cache versus one instance per ViewModel graph.</summary>
public static class DiscordGuildIconCache
{
    private static readonly HttpClient Http = new();
    private static readonly Dictionary<string, BitmapImage?> Cache = new();
    private static readonly Dictionary<string, Task<BitmapImage?>> InFlight = new();

    /// <summary>Non-blocking: returns the cached bitmap if already loaded (or already known to
    /// have failed, cached as null), or null if a load hasn't completed yet - callers are
    /// expected to also call LoadAsync and re-check once it resolves, same pattern as
    /// PhotoViewModel.Thumbnail's own lazy-async load.</summary>
    public static BitmapImage? TryGet(string iconUrl) => Cache.GetValueOrDefault(iconUrl);

    public static async Task<BitmapImage?> LoadAsync(string iconUrl)
    {
        if (Cache.TryGetValue(iconUrl, out var cached)) return cached;
        if (InFlight.TryGetValue(iconUrl, out var pending)) return await pending;

        var task = LoadCoreAsync(iconUrl);
        InFlight[iconUrl] = task;
        try
        {
            return await task;
        }
        finally
        {
            InFlight.Remove(iconUrl);
        }
    }

    private static async Task<BitmapImage?> LoadCoreAsync(string iconUrl)
    {
        BitmapImage? result;
        try
        {
            byte[] bytes = await Http.GetByteArrayAsync(iconUrl);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            result = bmp;
        }
        catch (HttpRequestException)
        {
            result = null;
        }
        Cache[iconUrl] = result;
        return result;
    }
}

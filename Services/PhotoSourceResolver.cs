using System.IO;
using System.Net.Http;

namespace VrcPhotoManager.Services;

/// <summary>The single chokepoint every consumer of a photo's file bytes goes through - see
/// docs/superpowers/VrcPhotoManager/specs/2026-08-23-multi-library-discord-design.md's
/// "Full-size cache + eviction" section. A local-folder photo resolves instantly with no I/O;
/// a Discord photo downloads-and-caches on demand (or re-uses an already-cached file, or
/// recovers from an expired CDN URL by re-fetching the source message).</summary>
public class PhotoSourceResolver(Data.PhotoRepository photoRepo, DiscordPhotoCacheService cache, DiscordApiClient? discordClient)
{
    private static readonly HttpClient Http = new();

    public async Task<string> ResolveLocalPathAsync(Models.Photo photo, CancellationToken ct = default)
    {
        // Local-folder photo: LocalPath is always already correct, no I/O needed.
        if (photo.RemoteSourceId is null)
        {
            return photo.LocalPath ?? throw new InvalidOperationException(
                $"Photo {photo.Id} has no RemoteSourceId and no LocalPath - not a valid photo of either kind.");
        }

        // Already cached and the file's still actually there (not evicted since).
        if (photo.LocalPath is not null && File.Exists(photo.LocalPath))
        {
            photoRepo.TouchLastAccessed(photo.Id);
            // Keep the in-memory Photo instance in sync with the DB write above - LocalPath/
            // FileSize are already correct here, but every caller holding this same Photo
            // reference (MainViewModel/PhotoViewModel) would otherwise keep seeing a stale
            // LastAccessedAt for the rest of the session.
            photo.LastAccessedAt = DateTime.UtcNow;
            return photo.LocalPath;
        }

        // Not cached (or evicted) - download it. RemoteSourceUrl should be set (Discord sync
        // always sets it), but fall back to re-fetching the source message if it's somehow
        // missing or the download itself 404s (older messages' CDN URLs can expire/rotate).
        string? url = photo.RemoteSourceUrl;
        byte[]? bytes = url is not null ? await TryDownloadAsync(url, ct) : null;

        if (bytes is null)
        {
            bytes = await RefetchAndDownloadAsync(photo, ct);
        }

        // Discord CDN URLs carry signed query params (?ex=...&is=...&hm=...) - strip them via
        // Uri.AbsolutePath before extracting the filename, since Path.GetFileName on the raw
        // URL would drag the whole query string into the cache path (illegal on Windows).
        string filename = url is not null ? Path.GetFileName(new Uri(url).AbsolutePath) : "photo.png";
        if (string.IsNullOrEmpty(filename)) filename = "photo.png";
        string cachePath = cache.GetCachePath(photo.RemoteSourceId, filename);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllBytesAsync(cachePath, bytes, ct);
        photoRepo.SetLocalPathAndAccessed(photo.Id, cachePath, bytes.Length);
        // Keep the in-memory Photo instance (the same one the caller passed in, and that every
        // MainViewModel/PhotoViewModel holding a reference to this photo already has) in sync
        // with the DB write just above - without this, LocalPath stays null in memory for the
        // rest of the app session even after a real, successful download (e.g. producing a
        // garbage upload filename from Path.GetFileName(null) downstream).
        photo.LocalPath = cachePath;
        photo.FileSize = bytes.Length;
        photo.LastAccessedAt = DateTime.UtcNow;
        return cachePath;
    }

    private static async Task<byte[]?> TryDownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            return await Http.GetByteArrayAsync(url, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>RemoteSourceId is "{messageId}_{attachmentIndex}" (see DiscordLibraryService) -
    /// split it back apart to re-fetch the source message and get a fresh attachment URL. The
    /// channel id needed to re-fetch the message comes from Photo.DiscordChannelId, denormalized
    /// from the owning Library at insert time (see PhotoRepository.UpsertDiscordPhoto) so this
    /// doesn't need a join back to Library on every resolve call.</summary>
    private async Task<byte[]> RefetchAndDownloadAsync(Models.Photo photo, CancellationToken ct)
    {
        if (discordClient is null)
        {
            throw new InvalidOperationException(
                $"Photo {photo.Id}'s cached original is missing and no Discord bot token is configured to re-fetch it.");
        }

        string[] parts = photo.RemoteSourceId!.Split('_');
        string messageId = parts[0];
        int attachmentIndex = int.Parse(parts[1]);

        string channelId = photo.DiscordChannelId
            ?? throw new InvalidOperationException(
                $"Photo {photo.Id} has RemoteSourceId but no DiscordChannelId - cannot re-fetch its source message.");

        var message = await discordClient.GetMessageAsync(channelId, messageId, ct)
            ?? throw new InvalidOperationException($"Discord message {messageId} no longer exists.");
        string freshUrl = message.Attachments[attachmentIndex].Url;

        return await Http.GetByteArrayAsync(freshUrl, ct);
    }
}

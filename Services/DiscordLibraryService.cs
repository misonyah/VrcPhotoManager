using System.Net.Http;

namespace VrcPhotoManager.Services;

public static class DiscordLibraryService
{
    private static readonly string[] ImageContentTypePrefixes = ["image/png", "image/jpeg", "image/webp"];

    private static bool IsImageAttachment(DiscordAttachment attachment) =>
        attachment.ContentType is not null && ImageContentTypePrefixes.Any(p => attachment.ContentType.StartsWith(p));

    /// <summary>Fetches Discord's own CDN-resized small version directly (?width=256 query
    /// param) rather than downloading the full original just to shrink it locally - see the
    /// design spec's "thumbnail eager, full-size on demand" principle.</summary>
    private static async Task<byte[]> FetchThumbnailAsync(HttpClient http, string attachmentUrl, CancellationToken ct)
    {
        string thumbnailUrl = attachmentUrl.Contains('?') ? $"{attachmentUrl}&width=256" : $"{attachmentUrl}?width=256";
        return await http.GetByteArrayAsync(thumbnailUrl, ct);
    }

    /// <summary>Paginates the channel's message history (resuming from library.LastSyncedMessageId
    /// on every sync after the first), inserting a Photo row per new image attachment. Paced with
    /// a short delay between pages' thumbnail fetches - Discord's CDN isn't covered by the
    /// documented REST rate-limit contract DiscordApiClient already handles, but a large channel's
    /// initial backfill (thousands of thumbnail requests) risks CDN throttling under a tight
    /// unthrottled loop, confirmed as a real design concern in the multi-library spec.</summary>
    public static async Task SyncChannelAsync(
        Models.Library library, DiscordApiClient client, Data.PhotoRepository photoRepo,
        Data.LibraryRepository libraryRepo, IProgress<string>? progress, CancellationToken ct)
    {
        if (library.DiscordChannelId is null) return;

        using var http = new HttpClient();
        // Discord's `after` param, when omitted entirely, returns the channel's MOST RECENT
        // messages rather than the oldest - see GetMessagesAsync's own doc comment: it's this
        // caller's job to walk pages forward from an appropriate starting cursor. Snowflake "0"
        // is before every real message id, so a first sync (no LastSyncedMessageId yet) starts
        // from the true beginning of channel history instead of grabbing only the newest page
        // and then immediately terminating (the page after "the newest message" is always empty).
        string cursor = library.LastSyncedMessageId ?? "0";
        int totalNew = 0, totalMessages = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await client.GetMessagesAsync(library.DiscordChannelId, cursor, ct);
            if (page.Count == 0) break;

            foreach (var message in page)
            {
                totalMessages++;
                var imageAttachments = message.Attachments.Where(IsImageAttachment).ToList();
                for (int i = 0; i < imageAttachments.Count; i++)
                {
                    var attachment = imageAttachments[i];
                    string remoteSourceId = $"{message.Id}_{i}";
                    if (photoRepo.GetByRemoteSourceId(remoteSourceId) is not null) continue;

                    byte[] thumbnail = await FetchThumbnailAsync(http, attachment.Url, ct);
                    photoRepo.UpsertDiscordPhoto(library.Id, remoteSourceId, attachment.Url, thumbnail, library.DiscordChannelId!);
                    totalNew++;
                    // Pacing: see this method's own doc comment - the CDN has no documented
                    // rate-limit contract to react to (unlike the REST calls above, which
                    // DiscordApiClient already handles via 429/Retry-After), so this is a plain
                    // fixed delay rather than a reactive backoff.
                    await Task.Delay(100, ct);
                }
            }

            cursor = page[^1].Id;
            libraryRepo.UpdateLastSynced(library.Id, DateTime.UtcNow, cursor);
            progress?.Report($"Syncing {library.DisplayName} (Discord)... {totalMessages} messages, {totalNew} new photos");

            if (page.Count < 100) break; // last page
        }
    }
}

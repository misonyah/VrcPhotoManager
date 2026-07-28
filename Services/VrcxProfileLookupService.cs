using Microsoft.Data.Sqlite;
using System.IO;
using System.Net.Http;

namespace VrcPhotoManager.Services;

/// <summary>
/// Bootstraps a person's reference photo from VRCX's own locally-cached avatar-change feed -
/// no VRChat API login of any kind. VRCX already resolves and caches, for every user it has
/// observed, a CDN thumbnail URL; that URL itself is unauthenticated (confirmed live:
/// api.vrchat.cloud/api/1/image/{fileId}/{ver}/{res} 302s to a signed CloudFront URL serving
/// image/png with no cookie needed). This only covers users VRCX has actually seen change
/// avatars while running nearby - not every stranger who ever appeared in a screenshot - so a
/// null result here is a normal, silent "not available for this person", not an error.
/// </summary>
public class VrcxProfileLookupService
{
    // Single-account machine (see project CLAUDE.md) - VRCX names this table after the local
    // account's VRChat user id with "usr_" stripped and dashes removed.
    private const string FeedAvatarTable = "usrf9065286b1f24b7fa00815fc2a117546_feed_avatar";

    private readonly string _vrcxDbPath;
    private readonly HttpClient _http;

    private VrcxProfileLookupService(string vrcxDbPath)
    {
        _vrcxDbPath = vrcxDbPath;
        // VRChat's CDN 403s any request with no User-Agent (Cloudflare bot protection) -
        // same header VrcdnApiClient already sends for panel.vrcdn.live.
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) VrcPhotoManager/1.0");
    }

    /// <summary>Mirrors FaceDetectionService.TryCreate/WdTaggerService.TryCreate - a missing
    /// VRCX install degrades to "bootstrap unavailable", never a startup crash.</summary>
    public static VrcxProfileLookupService? TryCreate(out string? error)
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCX", "VRCX.sqlite3");
        if (!File.Exists(path))
        {
            error = $"VRCX database not found at {path}";
            return null;
        }
        error = null;
        return new VrcxProfileLookupService(path);
    }

    public async Task<byte[]?> TryFetchLatestThumbnailAsync(string vrcUserId, CancellationToken ct = default)
    {
        string? url = TryGetLatestThumbnailUrl(vrcUserId);
        if (url is null) return null;

        try
        {
            return await _http.GetByteArrayAsync(url, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Opens VRCX's database read-only - safe to query while VRCX is running (WAL
    /// mode), never writes to a file that isn't ours. Any failure (table missing, no rows for
    /// this user, corrupt db) degrades to null rather than throwing.</summary>
    private string? TryGetLatestThumbnailUrl(string vrcUserId)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_vrcxDbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT current_avatar_thumbnail_image_url FROM "{FeedAvatarTable}"
                WHERE user_id = @userId ORDER BY created_at DESC LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@userId", vrcUserId);
            return cmd.ExecuteScalar() as string;
        }
        catch
        {
            return null;
        }
    }
}

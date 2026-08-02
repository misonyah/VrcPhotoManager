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
    // Single-account machine (see project CLAUDE.md) - VRCX names these tables after the local
    // account's VRChat user id with "usr_" stripped and dashes removed.
    private const string LocalAccountIdNoPrefix = "f9065286b1f24b7fa00815fc2a117546";
    private const string FeedAvatarTable = $"usr{LocalAccountIdNoPrefix}_feed_avatar";
    private const string FriendLogTable = $"usr{LocalAccountIdNoPrefix}_friend_log_current";
    private const string FriendLogHistoryTable = $"usr{LocalAccountIdNoPrefix}_friend_log_history";
    private const string JoinLeaveTable = "gamelog_join_leave";
    private const string NotesTable = $"usr{LocalAccountIdNoPrefix}_notes";
    private const string FeedBioTable = $"usr{LocalAccountIdNoPrefix}_feed_bio";

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

    /// <summary>
    /// The local account isn't its own VRCX "friend" - friend_log_current only lists OTHER
    /// people, so tagging yourself in a photo needs a separate lookup: the logged-in user id
    /// from VRCX's own configs table, and the most recent display name VRCX logged for that id
    /// from the game log (join/leave events record the local player same as everyone else in
    /// the instance - VRCX has no dedicated "my own profile" table). Degrades to null if
    /// either piece is missing, same as every other lookup here.
    /// </summary>
    public (string UserId, string DisplayName)? GetSelf()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_vrcxDbPath};Mode=ReadOnly");
            conn.Open();

            using var idCmd = conn.CreateCommand();
            idCmd.CommandText = "SELECT value FROM configs WHERE key = 'config:lastuserloggedin'";
            if (idCmd.ExecuteScalar() is not string userId || userId.Length == 0) return null;

            using var nameCmd = conn.CreateCommand();
            nameCmd.CommandText = $"""
                SELECT display_name FROM "{JoinLeaveTable}"
                WHERE user_id = @userId ORDER BY created_at DESC LIMIT 1
                """;
            nameCmd.Parameters.AddWithValue("@userId", userId);
            return nameCmd.ExecuteScalar() is string displayName ? (userId, displayName) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Every friend VRCX currently has recorded for the local account - powers the "new
    /// person" name autocomplete in Tag Faces, so a tagged name's spelling comes straight from
    /// VRCX's own friends list instead of manual (typo-prone) typing. Same read-only, degrade-
    /// to-empty safety as the thumbnail lookup above.
    /// </summary>
    public List<(string UserId, string DisplayName)> GetFriends()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_vrcxDbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""SELECT user_id, display_name FROM "{FriendLogTable}" ORDER BY display_name COLLATE NOCASE""";
            using var reader = cmd.ExecuteReader();

            var result = new List<(string, string)>();
            while (reader.Read())
            {
                result.Add((reader.GetString(0), reader.GetString(1)));
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Every player this account's own gamelog has ever recorded a resolved user id for - any
    /// instance ever joined, not just current friends. Expands the Tag Faces autocomplete
    /// beyond "GetFriends" (found via a real report: "Lumiichu" had a resolved id in
    /// gamelog_join_leave but was never a friend, so friends-only search never found them).
    /// Deduped by user id via SQLite's "bare column alongside MAX() in the same GROUP BY picks
    /// that row's value" behavior (a documented SQLite-specific extension, not standard SQL) -
    /// keeps the most recently-seen display name per person, since people rename.
    /// </summary>
    public List<(string UserId, string DisplayName)> GetGamelogSeenPlayers()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_vrcxDbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT user_id, display_name, MAX(created_at) FROM "{JoinLeaveTable}"
                WHERE user_id IS NOT NULL AND user_id != ''
                GROUP BY user_id
                """;
            using var reader = cmd.ExecuteReader();

            var result = new List<(string, string)>();
            while (reader.Read())
            {
                result.Add((reader.GetString(0), reader.GetString(1)));
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Every explicit rename VRCX has recorded for a friend (previous_display_name from a
    /// 'DisplayName'-type history event) - a candidate alias; the current name is already
    /// covered by GetFriends. friend_log_history is friendship-scoped, so this only covers
    /// people who are/were friends - GetGamelogNameHistory covers non-friends too.
    /// </summary>
    public List<(string UserId, string Alias)> GetFriendRenameHistory()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_vrcxDbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT user_id, previous_display_name FROM "{FriendLogHistoryTable}"
                WHERE type = 'DisplayName' AND previous_display_name IS NOT NULL AND previous_display_name != ''
                """;
            using var reader = cmd.ExecuteReader();

            var result = new List<(string, string)>();
            while (reader.Read())
            {
                result.Add((reader.GetString(0), reader.GetString(1)));
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Every distinct display name gamelog_join_leave has ever recorded for a resolved user
    /// id - broader than GetFriendRenameHistory (works for non-friends too), less precise
    /// (just "different name strings logged for this id over time", not an explicit rename
    /// event), but a user id genuinely identifies one person, so this carries no real false-
    /// positive risk. Includes the current/latest name too - callers filter that out against
    /// whatever they already treat as the primary name.
    /// </summary>
    public List<(string UserId, string Alias)> GetGamelogNameHistory()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_vrcxDbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT DISTINCT user_id, display_name FROM "{JoinLeaveTable}"
                WHERE user_id IS NOT NULL AND user_id != ''
                """;
            using var reader = cmd.ExecuteReader();

            var result = new List<(string, string)>();
            while (reader.Read())
            {
                result.Add((reader.GetString(0), reader.GetString(1)));
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    public record NoteAndBio(string? Note, string? Bio);

    /// <summary>
    /// Batched lookup of VRCX's own per-friend note (usr..._notes, user-authored inside VRCX)
    /// and latest bio (usr..._feed_bio, a change-history log - this takes the single most
    /// recent row per user id). Neither is imported/stored anywhere in VrcPhotoManager: both
    /// are edited/observed exclusively on the VRCX side, so persisting a copy here would just
    /// be a second, staleness-prone source of truth with no write path back to correct it.
    /// Callers pass exactly the VrcUserIds already about to be shown (e.g. one popup's worth
    /// of suggestions) - same "cheap enough to query live" reasoning as GetFriends. Degrades
    /// to an empty dictionary on any failure, same as every other lookup in this class.
    /// </summary>
    public Dictionary<string, NoteAndBio> GetNotesAndBios(IEnumerable<string> vrcUserIds)
    {
        var ids = vrcUserIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        var result = new Dictionary<string, NoteAndBio>();

        try
        {
            using var conn = new SqliteConnection($"Data Source={_vrcxDbPath};Mode=ReadOnly");
            conn.Open();

            string placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));

            // Notes and bios are queried against two different, independently-fallible VRCX
            // tables - each block gets its own try/catch (rather than one shared around both)
            // so a bio-query failure (e.g. usr..._feed_bio missing on some VRCX version) can
            // never discard notes already read, and vice versa. See class doc comment: a user
            // id present in only one source should still get an entry with the other field null.
            try
            {
                using var noteCmd = conn.CreateCommand();
                noteCmd.CommandText = $"""
                    SELECT user_id, note FROM "{NotesTable}" WHERE user_id IN ({placeholders})
                    """;
                for (int i = 0; i < ids.Count; i++) noteCmd.Parameters.AddWithValue($"@id{i}", ids[i]);
                using var reader = noteCmd.ExecuteReader();
                while (reader.Read())
                {
                    string userId = reader.GetString(0);
                    string? note = reader.IsDBNull(1) ? null : reader.GetString(1);
                    result[userId] = new NoteAndBio(note, null);
                }
            }
            catch
            {
                // Degrade to "no notes" - bios below still get a chance to populate result.
            }

            try
            {
                using var bioCmd = conn.CreateCommand();
                // ROW_NUMBER() picks the single most-recent feed_bio row per user_id (bio
                // history has many rows per person - one per change VRCX observed).
                bioCmd.CommandText = $"""
                    SELECT user_id, bio FROM (
                        SELECT user_id, bio,
                               ROW_NUMBER() OVER (PARTITION BY user_id ORDER BY created_at DESC, id DESC) AS rn
                        FROM "{FeedBioTable}" WHERE user_id IN ({placeholders})
                    ) WHERE rn = 1
                    """;
                for (int i = 0; i < ids.Count; i++) bioCmd.Parameters.AddWithValue($"@id{i}", ids[i]);
                using var reader = bioCmd.ExecuteReader();
                while (reader.Read())
                {
                    string userId = reader.GetString(0);
                    string? bio = reader.IsDBNull(1) ? null : reader.GetString(1);
                    result[userId] = result.TryGetValue(userId, out var existing)
                        ? existing with { Bio = bio }
                        : new NoteAndBio(null, bio);
                }
            }
            catch
            {
                // Degrade to "no bios" - notes already read above are kept.
            }

            return result;
        }
        catch
        {
            return result;
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

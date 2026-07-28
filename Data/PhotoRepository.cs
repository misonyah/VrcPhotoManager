using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VrcdnManager.Models;

namespace VrcdnManager.Data;

public class PhotoRepository
{
    private readonly string _dbPath;

    public PhotoRepository(string dbPath)
    {
        _dbPath = dbPath;
        EnsureDatabaseUpToDate();
    }

    private VrcdnDbContext NewContext() => new(_dbPath);

    /// <summary>
    /// The db predates EF Core (hand-created via raw SQL). If it already has a `photos`
    /// table but no `__EFMigrationsHistory`, this is that legacy db being opened by EF for
    /// the first time: mark the initial migration as already applied (its CREATE TABLE
    /// matches the existing schema exactly) rather than let Migrate() try to recreate
    /// tables that already exist. A brand new install has neither table, so Migrate()
    /// just creates everything fresh with no bootstrapping needed.
    /// </summary>
    private void EnsureDatabaseUpToDate()
    {
        using var context = NewContext();

        bool historyExists = TableExists(context, "__EFMigrationsHistory");
        bool photosExists = TableExists(context, "photos");

        if (!historyExists && photosExists)
        {
            // GetMigrations() returns every migration in chronological order regardless of
            // applied state - the first one defined is always InitialCreate.
            string initialMigrationId = context.Database.GetMigrations().First();

            context.Database.ExecuteSqlRaw("""
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                )
                """);
            context.Database.ExecuteSqlRaw(
                """INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ({0}, {1})""",
                initialMigrationId, "10.0.10");
        }

        context.Database.Migrate();
    }

    private static bool TableExists(VrcdnDbContext context, string tableName)
    {
        using var conn = new SqliteConnection(context.Database.GetConnectionString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = @name";
        cmd.Parameters.AddWithValue("@name", tableName);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    public long UpsertLocalFile(string path, long size, double mtime)
    {
        using var context = NewContext();
        var existing = context.Photos.FirstOrDefault(p => p.LocalPath == path);
        if (existing is not null)
        {
            existing.FileSize = size;
            existing.Mtime = mtime;
            context.SaveChanges();
            return existing.Id;
        }

        var photo = new Photo { LocalPath = path, FileSize = size, Mtime = mtime };
        context.Photos.Add(photo);
        context.SaveChanges();
        return photo.Id;
    }

    /// <summary>
    /// Deliberately excludes the thumbnail BLOB - loading all of them upfront would defeat
    /// the point of lazy per-row loading. Use GetThumbnail(id) on demand (PhotoViewModel does
    /// this only when a row is actually realized by the virtualizing panel).
    /// </summary>
    public List<Photo> GetAll()
    {
        using var context = NewContext();
        return context.Photos
            .AsNoTracking()
            .OrderBy(p => p.LocalPath)
            .Select(p => new Photo
            {
                Id = p.Id,
                LocalPath = p.LocalPath,
                FileSize = p.FileSize,
                Mtime = p.Mtime,
                Width = p.Width,
                Height = p.Height,
                FileHash = p.FileHash,
                HasThumbnail = p.Thumbnail != null,
                Rating = p.Rating,
                MetadataScanned = p.MetadataScanned,
                AuthorId = p.AuthorId,
                AuthorDisplayName = p.AuthorDisplayName,
                WorldName = p.WorldName,
                PlayerNames = p.PlayerNames,
                Selected = p.Selected,
                RemoteStatus = p.RemoteStatus,
                RemoteUrl = p.RemoteUrl,
                RemoteId = p.RemoteId,
                UploadedAt = p.UploadedAt,
            })
            .ToList();
    }

    public byte[]? GetThumbnail(long id)
    {
        using var context = NewContext();
        return context.Photos.Where(p => p.Id == id).Select(p => p.Thumbnail).FirstOrDefault();
    }

    public void SetThumbnail(long id, byte[] thumbnail)
    {
        using var context = NewContext();
        context.Photos.Where(p => p.Id == id).ExecuteUpdate(s => s.SetProperty(p => p.Thumbnail, thumbnail));
    }

    public void SetImageDimensions(long id, int width, int height)
    {
        using var context = NewContext();
        context.Photos.Where(p => p.Id == id)
            .ExecuteUpdate(s => s.SetProperty(p => p.Width, width).SetProperty(p => p.Height, height));
    }

    public void SetVrcxMetadata(
        long id, string? authorId, string? authorDisplayName, string? worldName, string? playerNames,
        IEnumerable<(string UserId, string DisplayName)>? players = null)
    {
        using var context = NewContext();
        context.Photos.Where(p => p.Id == id).ExecuteUpdate(s => s
            .SetProperty(p => p.MetadataScanned, true)
            .SetProperty(p => p.AuthorId, authorId)
            .SetProperty(p => p.AuthorDisplayName, authorDisplayName)
            .SetProperty(p => p.WorldName, worldName)
            .SetProperty(p => p.PlayerNames, playerNames));

        // Replace this photo's stored players (re-scanning shouldn't accumulate duplicates -
        // same idempotency approach as FaceRepository.InsertDetectedFaces).
        context.PhotoPlayers.Where(p => p.PhotoId == id).ExecuteDelete();
        if (players is not null)
        {
            foreach (var player in players)
            {
                context.PhotoPlayers.Add(new PhotoPlayer { PhotoId = id, UserId = player.UserId, DisplayName = player.DisplayName });
            }
            context.SaveChanges();
        }
    }

    public void SetFileHash(long id, string hash)
    {
        using var context = NewContext();
        context.Photos.Where(p => p.Id == id).ExecuteUpdate(s => s.SetProperty(p => p.FileHash, hash));
    }

    public void SetSelected(long id, bool selected)
    {
        using var context = NewContext();
        context.Photos.Where(p => p.Id == id).ExecuteUpdate(s => s.SetProperty(p => p.Selected, selected));
    }

    public void SetRating(long id, string? rating)
    {
        using var context = NewContext();
        context.Photos.Where(p => p.Id == id).ExecuteUpdate(s => s.SetProperty(p => p.Rating, rating));
    }

    public void SetRatingByFileName(string fileName, string rating)
    {
        using var context = NewContext();
        context.Photos.Where(p => p.LocalPath.EndsWith(fileName))
            .ExecuteUpdate(s => s.SetProperty(p => p.Rating, rating));
    }

    public void UpdateRemoteStatus(long id, RemoteStatus status, string? remoteUrl = null, string? remoteId = null, string? uploadedAt = null)
    {
        using var context = NewContext();
        var photo = context.Photos.First(p => p.Id == id);
        photo.RemoteStatus = status;
        photo.RemoteUrl = remoteUrl ?? photo.RemoteUrl;
        photo.RemoteId = remoteId ?? photo.RemoteId;
        photo.UploadedAt = uploadedAt ?? photo.UploadedAt;
        context.SaveChanges();
    }

    /// <summary>
    /// Resets a photo back to NotUploaded and clears its remote identifiers - used when
    /// removing an object from VRCDN. Unlike UpdateRemoteStatus, this explicitly nulls
    /// RemoteUrl/RemoteId/UploadedAt rather than leaving them (its null-coalescing update
    /// only ever adds values, never clears them).
    /// </summary>
    public void ClearRemoteStatus(long id)
    {
        using var context = NewContext();
        var photo = context.Photos.First(p => p.Id == id);
        photo.RemoteStatus = RemoteStatus.NotUploaded;
        photo.RemoteUrl = null;
        photo.RemoteId = null;
        photo.UploadedAt = null;
        context.SaveChanges();
    }

    // Objects uploaded by this app keep the real local filename verbatim, so a literal
    // suffix match handles those. Objects uploaded by the older Python pipeline
    // (vrcdn_upload.py) were staged under a reformatted name first - lowercased, dashes and
    // colons stripped - so ~76% of the 2,339 pre-existing uploads look like
    // "vrchat_20251009_234906933_7680x4320.jpg" instead of the real
    // "VRChat_2025-10-09_23-49-06.933_7680x4320.png". Neither name is a substring of the
    // other, so those need matching on the embedded date+time+resolution instead.
    private static readonly Regex UploadedNamePattern = new(
        @"^vrchat_(?<date>\d{8})_(?<time>\d{9})_(?<res>\d+x\d+)\.jpg$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LocalNameDateFirstPattern = new(
        @"^VRChat_(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2})_(?<h>\d{2})-(?<mi>\d{2})-(?<s>\d{2})\.(?<ms>\d{3})_(?<res>\d+x\d+)\.",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LocalNameResFirstPattern = new(
        @"^VRChat_(?<res>\d+x\d+)_(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2})_(?<h>\d{2})-(?<mi>\d{2})-(?<s>\d{2})\.(?<ms>\d{3})\.",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Normalized "date-time-resolution" key so an uploaded name and a local
    /// filename can be compared even though neither is a substring of the other.</summary>
    private static string? TryParseUploadedNameKey(string uploadedName)
    {
        var m = UploadedNamePattern.Match(uploadedName);
        return m.Success ? $"{m.Groups["date"].Value}-{m.Groups["time"].Value}-{m.Groups["res"].Value.ToLowerInvariant()}" : null;
    }

    private static string? TryParseLocalNameKey(string localPath)
    {
        string fileName = Path.GetFileName(localPath);
        var m = LocalNameDateFirstPattern.Match(fileName);
        m = m.Success ? m : LocalNameResFirstPattern.Match(fileName);
        if (!m.Success) return null;

        string date = $"{m.Groups["y"].Value}{m.Groups["mo"].Value}{m.Groups["d"].Value}";
        string time = $"{m.Groups["h"].Value}{m.Groups["mi"].Value}{m.Groups["s"].Value}{m.Groups["ms"].Value}";
        return $"{date}-{time}-{m.Groups["res"].Value.ToLowerInvariant()}";
    }

    /// <summary>
    /// Matches remote objects against local rows and marks the first unclaimed match per
    /// object as Uploaded. Tries an exact filename-suffix match first (objects this app
    /// itself uploaded), then falls back to the normalized date/time/resolution match above
    /// (objects the older Python pipeline uploaded under a reformatted name). Returns the
    /// original names of remote objects that matched neither - either genuinely not part of
    /// this local library (e.g. curated/video-derived content also uploaded for the
    /// photo-frame slideshow) or an unhandled filename shape (~4% of local files have
    /// irregular names - a missing leading zero, an extra suffix, etc.).
    ///
    /// Matches against ALL local photos, not just ones still NotUploaded - a re-sync must
    /// not report a remote object as "unresolved" just because its local twin was already
    /// correctly marked Uploaded by an earlier sync (an earlier version of this method did
    /// exactly that, making every re-sync look like it regressed to 0 matches).
    /// </summary>
    public List<string> SyncRemoteMatches(IEnumerable<(string OriginalFileName, string Id, string Extension, long Size)> remoteObjects, string vrcdnUsername)
    {
        using var context = NewContext();

        // Loaded once and matched in memory - the fallback needs regex parsing that EF can't
        // translate to SQL, and re-querying per remote object would be thousands of round trips.
        var candidates = context.Photos
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.LocalPath, p.RemoteStatus })
            .ToList();
        var statusById = candidates.ToDictionary(c => c.Id, c => c.RemoteStatus);
        var byNormalizedKey = candidates
            .Select(c => (c.Id, c.LocalPath, Key: TryParseLocalNameKey(c.LocalPath)))
            .Where(c => c.Key is not null)
            .ToLookup(c => c.Key!);

        var claimed = new HashSet<long>();
        var unresolved = new List<string>();

        foreach (var obj in remoteObjects)
        {
            long? matchId = candidates.FirstOrDefault(c => !claimed.Contains(c.Id) && c.LocalPath.EndsWith(obj.OriginalFileName))?.Id;

            if (matchId is null)
            {
                string? key = TryParseUploadedNameKey(obj.OriginalFileName);
                if (key is not null)
                {
                    matchId = byNormalizedKey[key]
                        .Where(c => !claimed.Contains(c.Id))
                        .Select(c => (long?)c.Id)
                        .FirstOrDefault();
                }
            }

            if (matchId is null)
            {
                unresolved.Add(obj.OriginalFileName);
                continue;
            }

            claimed.Add(matchId.Value);
            if (statusById[matchId.Value] == RemoteStatus.Uploaded) continue; // already correct from a prior sync

            var photo = context.Photos.First(p => p.Id == matchId.Value);
            photo.RemoteStatus = RemoteStatus.Uploaded;
            photo.RemoteUrl = $"https://vrcdn.cloud/{vrcdnUsername}/{obj.Id}.{obj.Extension}";
            photo.RemoteId = obj.Id;
            context.SaveChanges();
        }
        return unresolved;
    }

    public byte[]? GetSetting(string key)
    {
        using var context = NewContext();
        return context.Settings.Where(s => s.Key == key).Select(s => s.Value).FirstOrDefault();
    }

    public void SetSetting(string key, byte[] value)
    {
        using var context = NewContext();
        var existing = context.Settings.FirstOrDefault(s => s.Key == key);
        if (existing is not null)
        {
            existing.Value = value;
        }
        else
        {
            context.Settings.Add(new AppSetting { Key = key, Value = value });
        }
        context.SaveChanges();
    }

    public string? GetStringSetting(string key)
    {
        byte[]? bytes = GetSetting(key);
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    public void SetStringSetting(string key, string value) =>
        SetSetting(key, System.Text.Encoding.UTF8.GetBytes(value));
}

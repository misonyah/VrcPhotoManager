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
    /// Matches remote objects (by filename only, VRCDN doesn't know local paths) against
    /// local rows and marks the first unclaimed match per filename as Uploaded. Returns the
    /// filenames of remote objects that didn't match any local row (or matched one already
    /// claimed - e.g. duplicate filenames from different folders).
    /// </summary>
    public List<string> SyncRemoteMatches(IEnumerable<(string OriginalFileName, string Id, string Extension, long Size)> remoteObjects, string vrcdnUsername)
    {
        using var context = NewContext();
        var unresolved = new List<string>();
        foreach (var obj in remoteObjects)
        {
            var match = context.Photos
                .Where(p => p.LocalPath.EndsWith(obj.OriginalFileName) && p.RemoteStatus != RemoteStatus.Uploaded)
                .OrderBy(p => p.Id)
                .FirstOrDefault();

            if (match is null)
            {
                unresolved.Add(obj.OriginalFileName);
                continue;
            }

            match.RemoteStatus = RemoteStatus.Uploaded;
            match.RemoteUrl = $"https://vrcdn.cloud/{vrcdnUsername}/{obj.Id}.{obj.Extension}";
            match.RemoteId = obj.Id;
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
}

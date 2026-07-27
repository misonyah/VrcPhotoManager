using Microsoft.Data.Sqlite;
using VrcdnManager.Models;

namespace VrcdnManager.Data;

public class PhotoRepository
{
    private readonly string _connectionString;

    public PhotoRepository(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        Initialize();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS photos (
                id INTEGER PRIMARY KEY,
                local_path TEXT UNIQUE NOT NULL,
                file_size INTEGER NOT NULL,
                mtime REAL NOT NULL,
                thumbnail_path TEXT,
                rating TEXT,
                selected INTEGER NOT NULL DEFAULT 0,
                remote_status TEXT NOT NULL DEFAULT 'NotUploaded',
                remote_url TEXT,
                remote_id TEXT,
                uploaded_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_photos_status ON photos(remote_status);

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value BLOB
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public long UpsertLocalFile(string path, long size, double mtime)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO photos (local_path, file_size, mtime)
            VALUES (@path, @size, @mtime)
            ON CONFLICT(local_path) DO UPDATE SET file_size = @size, mtime = @mtime
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@size", size);
        cmd.Parameters.AddWithValue("@mtime", mtime);
        return (long)cmd.ExecuteScalar()!;
    }

    public List<Photo> GetAll()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, local_path, file_size, mtime, thumbnail_path, rating, selected, remote_status, remote_url, remote_id, uploaded_at FROM photos ORDER BY local_path";
        using var reader = cmd.ExecuteReader();
        var result = new List<Photo>();
        while (reader.Read())
        {
            result.Add(ReadPhoto(reader));
        }
        return result;
    }

    private static Photo ReadPhoto(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        LocalPath = reader.GetString(1),
        FileSize = reader.GetInt64(2),
        Mtime = reader.GetDouble(3),
        ThumbnailPath = reader.IsDBNull(4) ? null : reader.GetString(4),
        Rating = reader.IsDBNull(5) ? null : reader.GetString(5),
        Selected = reader.GetInt64(6) != 0,
        RemoteStatus = Enum.Parse<RemoteStatus>(reader.GetString(7)),
        RemoteUrl = reader.IsDBNull(8) ? null : reader.GetString(8),
        RemoteId = reader.IsDBNull(9) ? null : reader.GetString(9),
        UploadedAt = reader.IsDBNull(10) ? null : reader.GetString(10),
    };

    public void SetThumbnailPath(long id, string thumbnailPath)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE photos SET thumbnail_path = @t WHERE id = @id";
        cmd.Parameters.AddWithValue("@t", thumbnailPath);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetSelected(long id, bool selected)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE photos SET selected = @s WHERE id = @id";
        cmd.Parameters.AddWithValue("@s", selected ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetRating(long id, string? rating)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE photos SET rating = @r WHERE id = @id";
        cmd.Parameters.AddWithValue("@r", (object?)rating ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetRatingByFileName(string fileName, string rating)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE photos SET rating = @r
            WHERE local_path LIKE '%' || @name
            """;
        cmd.Parameters.AddWithValue("@r", rating);
        cmd.Parameters.AddWithValue("@name", fileName);
        cmd.ExecuteNonQuery();
    }

    public void UpdateRemoteStatus(long id, RemoteStatus status, string? remoteUrl = null, string? remoteId = null, string? uploadedAt = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE photos
            SET remote_status = @status,
                remote_url = COALESCE(@url, remote_url),
                remote_id = COALESCE(@rid, remote_id),
                uploaded_at = COALESCE(@uploadedAt, uploaded_at)
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@status", status.ToString());
        cmd.Parameters.AddWithValue("@url", (object?)remoteUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rid", (object?)remoteId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@uploadedAt", (object?)uploadedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Matches remote objects (by filename only, VRCDN doesn't know local paths) against
    /// local rows and marks the first unclaimed match per filename as Uploaded. Returns the
    /// filenames of remote objects that didn't match any local row (or matched one already
    /// claimed - e.g. duplicate filenames from different folders).
    /// </summary>
    public List<string> SyncRemoteMatches(IEnumerable<(string OriginalFileName, string Id, string Extension, long Size)> remoteObjects, string vrcdnUsername)
    {
        using var conn = Open();
        var unresolved = new List<string>();
        foreach (var obj in remoteObjects)
        {
            using var findCmd = conn.CreateCommand();
            findCmd.CommandText = """
                SELECT id FROM photos
                WHERE local_path LIKE '%' || @name
                  AND remote_status != 'Uploaded'
                ORDER BY id LIMIT 1
                """;
            findCmd.Parameters.AddWithValue("@name", obj.OriginalFileName);
            var idResult = findCmd.ExecuteScalar();
            if (idResult is null)
            {
                unresolved.Add(obj.OriginalFileName);
                continue;
            }

            using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = """
                UPDATE photos SET remote_status = 'Uploaded', remote_url = @url, remote_id = @rid
                WHERE id = @id
                """;
            updateCmd.Parameters.AddWithValue("@url", $"https://vrcdn.cloud/{vrcdnUsername}/{obj.Id}.{obj.Extension}");
            updateCmd.Parameters.AddWithValue("@rid", obj.Id);
            updateCmd.Parameters.AddWithValue("@id", (long)idResult);
            updateCmd.ExecuteNonQuery();
        }
        return unresolved;
    }

    public byte[]? GetSetting(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = @k";
        cmd.Parameters.AddWithValue("@k", key);
        var result = cmd.ExecuteScalar();
        return result as byte[];
    }

    public void SetSetting(string key, byte[] value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES (@k, @v)
            ON CONFLICT(key) DO UPDATE SET value = @v
            """;
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }
}

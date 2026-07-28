using System.ComponentModel.DataAnnotations.Schema;

namespace VrcdnManager.Models;

public enum RemoteStatus
{
    NotUploaded,
    Uploading,
    Uploaded,
    Failed,
}

public class Photo
{
    public long Id { get; set; }
    public required string LocalPath { get; set; }
    public long FileSize { get; set; }
    public double Mtime { get; set; }

    /// <summary>Pixel dimensions - avoids re-probing the file just to sort/filter by size.</summary>
    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>
    /// SHA256 of the file contents - catches true duplicates regardless of filename/path
    /// (the "slideshow\ folder" duplicates found earlier this session were only caught by
    /// exact filename match, which is a weaker signal than content hash).
    /// </summary>
    public string? FileHash { get; set; }

    public byte[]? Thumbnail { get; set; }
    public string? Rating { get; set; }

    /// <summary>
    /// VRCX embeds author/world/player info directly into the photo's PNG metadata at
    /// capture time - not every photo has it (needs VRCX running with that feature active).
    /// MetadataScanned distinguishes "checked, there was none" from "not checked yet" so
    /// Scan Library doesn't keep re-parsing PNG chunks for photos that genuinely lack it.
    /// </summary>
    public bool MetadataScanned { get; set; }
    public string? AuthorId { get; set; }
    public string? AuthorDisplayName { get; set; }
    public string? WorldName { get; set; }

    /// <summary>Comma-joined player display names - flat text is enough for substring
    /// filtering (the actual use case) without needing a join table. Stable per-player user
    /// ids live in the separate PhotoPlayer table instead, since display names can change
    /// over time but ids don't.</summary>
    public string? PlayerNames { get; set; }

    public bool Selected { get; set; }
    public RemoteStatus RemoteStatus { get; set; } = RemoteStatus.NotUploaded;
    public string? RemoteUrl { get; set; }
    public string? RemoteId { get; set; }
    public string? UploadedAt { get; set; }

    /// <summary>
    /// Only ever populated by a query that deliberately projects it (never loading the
    /// Thumbnail blob for the whole library at once) - not a real column.
    /// </summary>
    [NotMapped]
    public bool HasThumbnail { get; set; }

    public string FileName => System.IO.Path.GetFileName(LocalPath);
}

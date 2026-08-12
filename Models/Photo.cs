using System.ComponentModel.DataAnnotations.Schema;

namespace VrcPhotoManager.Models;

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
    public string? AvatarType { get; set; }
    public float? AvatarTypeConfidence { get; set; }

    /// <summary>The stable "booth:&lt;item id&gt;"/"local:NNNN" identity for AvatarType (see
    /// the avatar-scraper tool's catalog_ids.py) - unlike AvatarType's display text, which can
    /// change as label-cleanup rules improve, this is meant to stay the same for a given avatar
    /// across re-classification. Null for photos classified before this existed, or against a
    /// model directory with no catalog_ids.txt.</summary>
    public string? AvatarCatalogId { get; set; }

    /// <summary>
    /// VRCX embeds author/world/player info directly into the photo's PNG metadata at
    /// capture time - not every photo has it (needs VRCX running with that feature active).
    /// MetadataScanned distinguishes "checked, there was none" from "not checked yet" so
    /// Scan Library doesn't keep re-parsing PNG chunks for photos that genuinely lack it.
    /// </summary>
    public bool MetadataScanned { get; set; }

    /// <summary>Same "checked, there was none" vs. "not checked yet" distinction as
    /// MetadataScanned, but for Detect Faces - a photo genuinely has zero faces in it
    /// sometimes, and without this flag that's indistinguishable from "never run the detector
    /// on this photo at all" (both have zero DetectedFaces rows), so Detect Faces would keep
    /// re-invoking the ML detector on it forever. Combined with whether any of the photo's
    /// DetectedFaces are still unresolved (see FaceRepository.GetPhotoIdsWithUnresolvedFaces),
    /// this lets a re-run skip photos with nothing left to find.</summary>
    public bool FacesScanned { get; set; }

    public string? AuthorId { get; set; }
    public string? AuthorDisplayName { get; set; }
    public string? WorldName { get; set; }
    public string? WorldId { get; set; }

    /// <summary>True only when WorldName was filled in by the gamelog cross-reference
    /// fallback (GamelogCorrelationService.TryGetWorldName) rather than VRChat's own embedded
    /// PNG metadata - lets the UI distinguish an authoritative value from an inferred one, same
    /// spirit as GamelogInferredPlayer being a separate table from PhotoPlayer.</summary>
    public bool WorldNameInferred { get; set; }

    public bool Selected { get; set; }
    public RemoteStatus RemoteStatus { get; set; } = RemoteStatus.NotUploaded;
    public string? RemoteUrl { get; set; }
    public string? RemoteId { get; set; }
    public string? UploadedAt { get; set; }

    /// <summary>Which crop preset (see MainViewModel.UploadCropPreset) was applied to this
    /// photo's currently-uploaded VRCDN copy, e.g. "3:4", "4:3", "Custom 5:7" - null means it
    /// was uploaded uncropped (its original aspect ratio) or hasn't been uploaded at all.
    /// Drives the "Uploaded as" filter, letting you find photos uploaded with a specific crop
    /// without re-deriving it from the remote filename's resolution suffix.</summary>
    public string? UploadCropMode { get; set; }

    /// <summary>Where within the source image the not-yet-applied upload crop is positioned,
    /// as a -1..1 fraction of the available slack on each axis (0 = centered, the previous
    /// fixed behavior; -1/+1 = pinned to one edge). Adjusted per-photo via arrow keys while
    /// hovering (see MainWindow's PreviewKeyDown + PhotoViewModel.NudgeCropOffset), remembered
    /// until the photo is actually uploaded (UploadSelectedAsync resets both back to 0 on
    /// success, since the crop that mattered has already been applied and baked into the
    /// uploaded file). Meaningless once RemoteStatus is Uploaded - the preview overlay only
    /// ever reads these for a not-yet-uploaded photo.</summary>
    public double CropOffsetX { get; set; }
    public double CropOffsetY { get; set; }

    /// <summary>
    /// Only ever populated by a query that deliberately projects it (never loading the
    /// Thumbnail blob for the whole library at once) - not a real column.
    /// </summary>
    [NotMapped]
    public bool HasThumbnail { get; set; }

    public string FileName => System.IO.Path.GetFileName(LocalPath);
}

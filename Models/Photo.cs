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

    /// <summary>FK into Library.Id - every photo belongs to exactly one library (a local folder
    /// or a Discord channel). Backfilled for all pre-existing rows by the AddLibraryAndPhoto
    /// LibraryId migration, which also seeds the original hardcoded VRChat screenshot folder as
    /// Library row 1 - see the migration's Up() for the exact seed logic.</summary>
    public long LibraryId { get; set; }

    /// <summary>Null for a Discord-sourced photo whose full-size original hasn't been
    /// downloaded/cached yet (or was evicted since) - see RemoteSourceUrl/RemoteSourceId and
    /// PhotoSourceResolver. Always non-null for a local-folder photo.</summary>
    public string? LocalPath { get; set; }

    /// <summary>Discord CDN attachment URL - used to download the full-size original on demand.
    /// Null for local-folder photos. Can go stale (Discord CDN URLs on older messages can
    /// expire/rotate) - PhotoSourceResolver re-fetches the source message for a fresh one on a
    /// 404 rather than treating that as a hard failure.</summary>
    public string? RemoteSourceUrl { get; set; }

    /// <summary>"{discordMessageId}_{attachmentIndex}" - the dedup key for Discord sync (see
    /// DiscordLibraryService.SyncChannelAsync), independent of RemoteSourceUrl since that can
    /// rotate but this never does. Null for local-folder photos. Unique when non-null.</summary>
    public string? RemoteSourceId { get; set; }

    /// <summary>Denormalized from the owning Library.DiscordChannelId, set once at insert time
    /// (PhotoRepository.UpsertDiscordPhoto, sourced from DiscordLibraryService.SyncChannelAsync's
    /// library.DiscordChannelId) - lets PhotoSourceResolver.RefetchAndDownloadAsync re-fetch the
    /// source message on a stale/expired CDN URL without a join back to Library on every resolve
    /// call. Null for local-folder photos.</summary>
    public string? DiscordChannelId { get; set; }

    /// <summary>Drives two-tier LRU eviction of the full-size cache (see
    /// DiscordPhotoCacheService) - set whenever PhotoSourceResolver hands out a cached local
    /// path, whether newly downloaded or already present. Null for local-folder photos (never
    /// evicted, so never needs this).</summary>
    public DateTime? LastAccessedAt { get; set; }

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

    /// <summary>FK into AvatarCatalog.Id - unlike AvatarType's display text, which can change as
    /// label-cleanup rules improve, this is meant to stay the same for a given avatar across
    /// re-classification. Resolved from the classifier's flat "booth:&lt;item id&gt;"/
    /// "local:NNNN" id (see AvatarTypeService.Classify, avatar-scraper's catalog_ids.py) via
    /// AvatarCatalogRepository.GetOrCreateByTrainedCatalogId. Null for photos classified before
    /// this existed, or against a model directory with no catalog_ids.txt.</summary>
    public long? AvatarCatalogId { get; set; }

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

    /// <summary>The image format ("jpg" or "png" - see SettingsKeys.UploadImageFormat) this app
    /// actually encoded and sent for this photo's current upload. Null means either not
    /// uploaded, or uploaded before this field existed. NOT derived from RemoteUrl's extension -
    /// confirmed live that VRCDN's ListObjects API reports ".png" for every object regardless of
    /// what was actually uploaded (every one of this app's own uploads is genuinely JPEG, yet
    /// 100% of RemoteUrls end in .png), so the URL is not a reliable signal of the real format.
    /// Drives the cloud badge's gray-vs-cyan color (PhotoViewModel.IsUploadedAsPng).</summary>
    public string? UploadedFormat { get; set; }

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

    /// <summary>Snapshot of CropOffsetX/Y at the moment this photo's current upload actually
    /// happened - the "what's really live" baseline. CropOffsetX/Y itself keeps moving as you
    /// nudge (even on an Uploaded photo - see PhotoViewModel.NudgeCropOffset), so comparing it
    /// against this baseline is how the app tells "just browsing, matches what's live" apart
    /// from "a real pending edit" (PhotoViewModel.HasPendingCropEdit) - the trigger for both the
    /// cyan selection-border hint and for Upload Selected re-uploading it. Reset to match
    /// CropOffsetX/Y again on a fresh successful upload.</summary>
    public double UploadedOffsetX { get; set; }
    public double UploadedOffsetY { get; set; }

    /// <summary>Per-photo override of which preset (see MainViewModel.UploadCropPreset.Name,
    /// e.g. "4:3 (Landscape)" or "Original (no crop)") this specific photo uploads as, instead
    /// of whatever the global dropdown has selected - cycled via the [ / ] keys while hovering
    /// (see PhotoViewModel.CycleCropRatioOverride), same "remembered until actually uploaded,
    /// then reset" lifecycle as CropOffsetX/Y. Null means "use the dropdown", the previous
    /// one-ratio-for-the-whole-batch behavior. Never set to "Custom..." - a keyboard cycle can't
    /// usefully drive that preset's free-text ratio, so it's skipped when cycling.</summary>
    public string? CropRatioOverride { get; set; }

    /// <summary>The VRCDN RemoteId this photo used to have before a re-upload (see
    /// PhotoViewModel.PrepareForReupload) reverted it to NotUploaded - kept around (rather than
    /// just discarded) so the NEXT successful upload of this photo can actually delete the old
    /// VRCDN object afterward (see MainViewModel.UploadSelectedAsync), instead of silently
    /// leaving it behind as an orphaned, quota-consuming duplicate. Cleared once that removal
    /// succeeds. Also drives the orange (vs. gold) selection-border hint in MainWindow.xaml -
    /// "this selected photo will replace something already on VRCDN when uploaded".</summary>
    public string? PendingRemovalRemoteId { get; set; }

    /// <summary>
    /// Only ever populated by a query that deliberately projects it (never loading the
    /// Thumbnail blob for the whole library at once) - not a real column.
    /// </summary>
    [NotMapped]
    public bool HasThumbnail { get; set; }

    /// <summary>Null when LocalPath is null (a Discord-sourced photo whose full-size original
    /// hasn't been cached yet) - see LocalPath's doc comment.</summary>
    public string? FileName => System.IO.Path.GetFileName(LocalPath);
}

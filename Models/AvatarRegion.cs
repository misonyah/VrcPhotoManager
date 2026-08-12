namespace VrcPhotoManager.Models;

/// <summary>A manually-drawn box marking which avatar a specific region of a photo shows -
/// unlike DetectedFace, there's no automatic detector for these (Classify Avatars only ever
/// produces one whole-photo guess), so every row here comes from Tag Faces' Avatar mode. Needed
/// for group photos with more than one avatar in frame, where a single Photo.AvatarType/
/// AvatarCatalogId can't represent "which person is wearing which avatar".</summary>
public class AvatarRegion
{
    public long Id { get; set; }
    public long PhotoId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>The stable catalog identity (see catalog_ids.py / Photo.AvatarCatalogId) for
    /// the avatar this region shows - null until tagged (a freshly-drawn box starts blank, same
    /// as a manually-drawn face box starts untagged).</summary>
    public string? AvatarCatalogId { get; set; }

    /// <summary>Display text snapshot at tag time - kept alongside AvatarCatalogId rather than
    /// re-resolved from labels.txt live, same "store a snapshot, not a live foreign lookup"
    /// convention as PhotoPlayer's DisplayName. A future labels.txt cleanup pass changing this
    /// avatar's display text shouldn't retroactively rewrite what the user actually saw and
    /// picked at tag time.</summary>
    public string? AvatarDisplayName { get; set; }

    public DateTime TaggedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag - same convention as DetectedFace.Deleted (see its own doc
    /// comment): a removed region stays in the table instead of being hard-deleted. Every read
    /// query must exclude Deleted rows.</summary>
    public bool Deleted { get; set; }
}

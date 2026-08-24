namespace VrcPhotoManager.Models;

/// <summary>A box marking which avatar a specific region of a photo shows - either manually
/// drawn (Tag Faces' Avatar mode, always Confirmed=true) or automatically detected
/// (AvatarBodyDetectionService + AvatarTypeService's per-region classification, starts
/// Confirmed=false pending human review, same as an EmbeddingMatch FaceLabel). Needed for group
/// photos with more than one avatar in frame, where a single Photo.AvatarType/AvatarCatalogId
/// can't represent "which person is wearing which avatar".</summary>
public class AvatarRegion
{
    public long Id { get; set; }
    public long PhotoId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>FK into AvatarCatalog.Id (see Photo.AvatarCatalogId) for the avatar this region
    /// shows - null until tagged (a freshly-drawn box starts blank, same as a manually-drawn
    /// face box starts untagged).</summary>
    public long? AvatarCatalogId { get; set; }

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

    /// <summary>True for every manually-drawn region (a human placing a box IS the confirm
    /// action - defaults true so the migration doesn't retroactively unconfirm every existing
    /// row) and for an auto-detected region once a human has reviewed/accepted it. False only
    /// for a fresh auto-detected-and-classified region awaiting review - shown as an orange
    /// dashed box in Tag Faces, same visual language as an unconfirmed face suggestion.</summary>
    public bool Confirmed { get; set; } = true;

    /// <summary>AvatarTypeService's classification confidence for this specific region - null
    /// for a manually-drawn/manually-tagged region (there's no model score to record), set for
    /// an auto-detected one regardless of whether it's since been confirmed.</summary>
    public float? Confidence { get; set; }
}

namespace VrcPhotoManager.Models;

/// <summary>An avatar's identity as a real entity - structured per-store product identity
/// (Booth/Gumroad/Jinxxy) plus an optional parent-avatar link for derivatives/recolors,
/// replacing the flat "booth:&lt;item id&gt;"/"local:NNNN" string that used to be stored
/// directly on Photo/AvatarRegion. See docs/superpowers/VrcPhotoManager/specs/
/// 2026-08-23-avatar-catalog-design.md (in the PC umbrella repo - VrcPhotoManager has a
/// public remote) for the full design.</summary>
public class AvatarCatalog
{
    public long Id { get; set; }

    /// <summary>The classifier's flat id ("booth:8612943"/"local:0007", see
    /// avatar-scraper/catalog_ids.py) when this row corresponds to a trained class. Null for
    /// avatars cataloged by hand that aren't (yet) trained - e.g. a parent avatar referenced
    /// only as lineage metadata.</summary>
    public string? TrainedCatalogId { get; set; }

    /// <summary>Snapshot for rows without a live label source (a manually-created parent entry
    /// has nothing else to show in pickers/search), same convention as
    /// AvatarRegion.AvatarDisplayName.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Booth item id, e.g. "8612943" - no uploader column, since Booth URLs
    /// (booth.pm/en/items/&lt;id&gt;) carry no uploader segment.</summary>
    public string? BoothProduct { get; set; }

    public string? GumroadUser { get; set; }
    public string? GumroadProduct { get; set; }
    public string? JinxxyUser { get; set; }
    public string? JinxxyProduct { get; set; }

    /// <summary>The base avatar this one derives from (recolor/edit) - self-referencing, null
    /// when this avatar isn't known to be based on another.</summary>
    public long? ParentItemId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

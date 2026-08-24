using Microsoft.EntityFrameworkCore;
using VrcPhotoManager.Models;

namespace VrcPhotoManager.Data;

/// <summary>CRUD for AvatarRegion (Tag Faces' Avatar mode) - a small, separate repository
/// rather than folding into FaceRepository/PhotoRepository, since avatar regions are their own
/// sub-entity (same relationship DetectedFace has to FaceRepository), just for avatar identity
/// instead of person identity.</summary>
public class AvatarRegionRepository(string dbPath)
{
    private VrcdnDbContext NewContext() => new(dbPath);

    public List<AvatarRegion> GetRegionsForPhoto(long photoId)
    {
        using var context = NewContext();
        return context.AvatarRegions.AsNoTracking()
            .Where(r => r.PhotoId == photoId && !r.Deleted)
            .OrderBy(r => r.Id)
            .ToList();
    }

    /// <summary>Photo ids with at least one region (manual or auto-detected) - lets
    /// MainViewModel.ClassifyAvatarsAsync skip a photo it already broke down per-region on a
    /// previous run, the same way it already skips a photo with a whole-photo AvatarType result.
    /// Without this, a multi-avatar photo (which deliberately never gets a Photo.AvatarType -
    /// see ClassifyAvatarsAsync) would look "still missing" and get re-detected/re-classified on
    /// every single run forever.</summary>
    public HashSet<long> GetPhotoIdsWithRegions()
    {
        using var context = NewContext();
        return context.AvatarRegions.AsNoTracking()
            .Where(r => !r.Deleted)
            .Select(r => r.PhotoId)
            .ToHashSet();
    }

    /// <summary>A freshly-drawn manual box (Tag Faces' Avatar mode) - Confirmed defaults to
    /// true (the model default), since a human placing the box by hand already is the confirm
    /// action, unlike an auto-detected region (see AddAutoDetectedRegion).</summary>
    public AvatarRegion AddRegion(long photoId, int x, int y, int width, int height)
    {
        using var context = NewContext();
        var region = new AvatarRegion { PhotoId = photoId, X = x, Y = y, Width = width, Height = height };
        context.AvatarRegions.Add(region);
        context.SaveChanges();
        return region;
    }

    /// <summary>AvatarBodyDetectionService + AvatarTypeService's per-region classification
    /// pipeline - Confirmed starts false (pending human review, same as an EmbeddingMatch
    /// FaceLabel), shown as an orange dashed box in Tag Faces until confirmed or corrected.</summary>
    public AvatarRegion AddAutoDetectedRegion(long photoId, int x, int y, int width, int height,
        long? avatarCatalogId, string? avatarDisplayName, float confidence)
    {
        using var context = NewContext();
        var region = new AvatarRegion
        {
            PhotoId = photoId, X = x, Y = y, Width = width, Height = height,
            AvatarCatalogId = avatarCatalogId, AvatarDisplayName = avatarDisplayName,
            Confidence = confidence, Confirmed = false,
        };
        context.AvatarRegions.Add(region);
        context.SaveChanges();
        return region;
    }

    /// <summary>A human explicitly picking a tag (fresh manual box, or overriding/correcting an
    /// auto-detected region's suggestion) - always fully confirms. Confidence is cleared: it
    /// would otherwise keep showing the auto-classifier's confidence for whatever it originally
    /// guessed, which is stale/misleading once a human has picked something specific (possibly
    /// different) themselves. See ConfirmRegion for accepting an existing suggestion as-is,
    /// which keeps the real Confidence instead.</summary>
    public void SetRegionTag(long regionId, long? avatarCatalogId, string? avatarDisplayName)
    {
        using var context = NewContext();
        context.AvatarRegions.Where(r => r.Id == regionId).ExecuteUpdate(s => s
            .SetProperty(r => r.AvatarCatalogId, avatarCatalogId)
            .SetProperty(r => r.AvatarDisplayName, avatarDisplayName)
            .SetProperty(r => r.Confirmed, true)
            .SetProperty(r => r.Confidence, (float?)null));
    }

    /// <summary>Accepts an auto-detected region's existing suggestion as-is (the "Confirm:
    /// {name}" quick-pick, same pattern as a face suggestion) - unlike SetRegionTag, this
    /// doesn't touch AvatarCatalogId/AvatarDisplayName/Confidence at all, just flips
    /// Confirmed.</summary>
    public void ConfirmRegion(long regionId)
    {
        using var context = NewContext();
        context.AvatarRegions.Where(r => r.Id == regionId)
            .ExecuteUpdate(s => s.SetProperty(r => r.Confirmed, true));
    }

    /// <summary>Soft-delete - same convention as FaceRepository.DeleteDetectedFace, kept for
    /// reviewing/undo purposes rather than hard-deleted.</summary>
    public void DeleteRegion(long regionId)
    {
        using var context = NewContext();
        context.AvatarRegions.Where(r => r.Id == regionId)
            .ExecuteUpdate(s => s.SetProperty(r => r.Deleted, true));
    }
}

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

    public AvatarRegion AddRegion(long photoId, int x, int y, int width, int height)
    {
        using var context = NewContext();
        var region = new AvatarRegion { PhotoId = photoId, X = x, Y = y, Width = width, Height = height };
        context.AvatarRegions.Add(region);
        context.SaveChanges();
        return region;
    }

    public void SetRegionTag(long regionId, string? avatarCatalogId, string? avatarDisplayName)
    {
        using var context = NewContext();
        context.AvatarRegions.Where(r => r.Id == regionId).ExecuteUpdate(s => s
            .SetProperty(r => r.AvatarCatalogId, avatarCatalogId)
            .SetProperty(r => r.AvatarDisplayName, avatarDisplayName));
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

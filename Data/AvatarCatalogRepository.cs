using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VrcPhotoManager.Models;

namespace VrcPhotoManager.Data;

/// <summary>CRUD for AvatarCatalog - structured multi-store avatar identity plus parent-avatar
/// lineage. See docs/superpowers/VrcPhotoManager/specs/2026-08-23-avatar-catalog-design.md (PC
/// umbrella repo) for the full design. A small, separate repository rather than folding into
/// PhotoRepository/AvatarRegionRepository, same reasoning as AvatarRegionRepository's own doc
/// comment: this is its own sub-entity with its own lifecycle.</summary>
public class AvatarCatalogRepository(string dbPath)
{
    private VrcdnDbContext NewContext() => new(dbPath);

    private static readonly Regex BoothIdRegex = new(@"^booth:(\d+)$", RegexOptions.Compiled);

    /// <summary>Resolves the classifier's flat id ("booth:8612943"/"local:0007", from
    /// AvatarTypeService.Classify/AllEntries) to a stable AvatarCatalog.Id, auto-creating a bare
    /// row on first sight (BoothProduct parsed out when the id is a "booth:" id; Gumroad/Jinxxy/
    /// Parent left null for later manual enrichment). Every classify-time and manual-pick write
    /// path resolves through here rather than storing the flat string directly, so
    /// Photo/AvatarRegion.AvatarCatalogId can be a real FK.</summary>
    public long GetOrCreateByTrainedCatalogId(string trainedCatalogId, string label)
    {
        using var context = NewContext();
        var existing = context.AvatarCatalogs.FirstOrDefault(c => c.TrainedCatalogId == trainedCatalogId);
        if (existing is not null) return existing.Id;

        var boothMatch = BoothIdRegex.Match(trainedCatalogId);
        var entry = new AvatarCatalog
        {
            TrainedCatalogId = trainedCatalogId,
            DisplayName = label,
            BoothProduct = boothMatch.Success ? boothMatch.Groups[1].Value : null,
        };
        context.AvatarCatalogs.Add(entry);
        context.SaveChanges();
        return entry.Id;
    }

    /// <summary>A manually-cataloged avatar with no trained class of its own - e.g. a parent
    /// avatar referenced only as lineage metadata (TrainedCatalogId stays null).</summary>
    public AvatarCatalog CreateManualEntry(string displayName)
    {
        using var context = NewContext();
        var entry = new AvatarCatalog { DisplayName = displayName };
        context.AvatarCatalogs.Add(entry);
        context.SaveChanges();
        return entry;
    }

    public AvatarCatalog? GetById(long id)
    {
        using var context = NewContext();
        return context.AvatarCatalogs.AsNoTracking().FirstOrDefault(c => c.Id == id);
    }

    /// <summary>Substring match against DisplayName across every cataloged avatar (trained or
    /// not) - used by the parent-avatar picker, unlike the avatar-tag picker's
    /// AvatarTypeService.AllEntries which only knows trained classes.</summary>
    public List<AvatarCatalog> Search(string query)
    {
        using var context = NewContext();
        var all = context.AvatarCatalogs.AsNoTracking();
        return (string.IsNullOrWhiteSpace(query)
                ? all
                : all.Where(c => c.DisplayName != null && c.DisplayName.Contains(query)))
            .OrderBy(c => c.DisplayName)
            .ToList();
    }

    public void Update(long id, string? boothProduct, string? gumroadUser, string? gumroadProduct,
        string? jinxxyUser, string? jinxxyProduct, long? parentItemId)
    {
        using var context = NewContext();
        context.AvatarCatalogs.Where(c => c.Id == id).ExecuteUpdate(s => s
            .SetProperty(c => c.BoothProduct, boothProduct)
            .SetProperty(c => c.GumroadUser, gumroadUser)
            .SetProperty(c => c.GumroadProduct, gumroadProduct)
            .SetProperty(c => c.JinxxyUser, jinxxyUser)
            .SetProperty(c => c.JinxxyProduct, jinxxyProduct)
            .SetProperty(c => c.ParentItemId, parentItemId));
    }
}

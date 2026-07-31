using Microsoft.EntityFrameworkCore;
using VrcPhotoManager.Models;
using VrcPhotoManager.Services;

namespace VrcPhotoManager.Data;

public class FaceRepository(string dbPath)
{
    private VrcdnDbContext NewContext() => new(dbPath);

    /// <summary>
    /// Replaces this photo's previously-detected faces (re-scanning shouldn't accumulate
    /// duplicates - a photo re-scanned twice should still have exactly one row per real face).
    /// </summary>
    public void InsertDetectedFaces(long photoId, IEnumerable<FaceBox> faces)
    {
        using var context = NewContext();
        context.DetectedFaces.Where(f => f.PhotoId == photoId).ExecuteDelete();
        foreach (var f in faces)
        {
            context.DetectedFaces.Add(new DetectedFace
            {
                PhotoId = photoId,
                X = f.X,
                Y = f.Y,
                Width = f.Width,
                Height = f.Height,
            });
        }
        context.SaveChanges();
    }

    /// <summary>
    /// Excludes faces confirmed as "&lt;unknown&gt;" (Confirmed=true, PersonId=null) - those are
    /// deliberately marked false-positive detections, not real faces, so they shouldn't count
    /// toward the thumbnail grid's per-photo face-count badge. Tagged counts only faces with a
    /// confirmed real-person label - an unconfirmed EmbeddingMatch suggestion (orange box) isn't
    /// "tagged" yet.
    /// </summary>
    public Dictionary<long, (int Total, int Tagged)> GetFaceCountsByPhoto()
    {
        using var context = NewContext();
        var notAFaceIds = context.FaceLabels
            .Where(l => l.Confirmed && l.PersonId == null)
            .Select(l => l.DetectedFaceId);
        var taggedFaceIds = context.FaceLabels
            .Where(l => l.Confirmed && l.PersonId != null)
            .Select(l => l.DetectedFaceId);

        var rows = context.DetectedFaces
            .Where(f => !notAFaceIds.Contains(f.Id))
            .Select(f => new { f.PhotoId, IsTagged = taggedFaceIds.Contains(f.Id) })
            .ToList();

        return rows
            .GroupBy(r => r.PhotoId)
            .ToDictionary(g => g.Key, g => (Total: g.Count(), Tagged: g.Count(r => r.IsTagged)));
    }

    /// <summary>Adds a single manually-drawn face box (the detector missed a real face) - same
    /// row shape as an auto-detected one, so it goes through the exact same tagging picker.</summary>
    public DetectedFace AddManualFace(long photoId, FaceBox box)
    {
        using var context = NewContext();
        var face = new DetectedFace
        {
            PhotoId = photoId,
            X = box.X,
            Y = box.Y,
            Width = box.Width,
            Height = box.Height,
        };
        context.DetectedFaces.Add(face);
        context.SaveChanges();
        return face;
    }

    public List<DetectedFace> GetDetectedFaces(long photoId)
    {
        using var context = NewContext();
        return context.DetectedFaces.AsNoTracking().Where(f => f.PhotoId == photoId).OrderBy(f => f.Id).ToList();
    }

    public Dictionary<long, FaceLabel> GetFaceLabelsByPhoto(long photoId)
    {
        using var context = NewContext();
        var faceIds = context.DetectedFaces.Where(f => f.PhotoId == photoId).Select(f => f.Id);
        return context.FaceLabels.AsNoTracking()
            .Where(l => faceIds.Contains(l.DetectedFaceId))
            .ToDictionary(l => l.DetectedFaceId, l => l);
    }

    /// <summary>
    /// Replaces any existing label for this face - a face has at most one label at a time,
    /// same re-scan idiom as InsertDetectedFaces.
    /// </summary>
    public void UpsertFaceLabel(long detectedFaceId, long? personId, bool confirmed, FaceLabelSource source, float confidence = 1.0f)
    {
        using var context = NewContext();
        long? previousPersonId = context.FaceLabels
            .Where(l => l.DetectedFaceId == detectedFaceId)
            .Select(l => l.PersonId)
            .FirstOrDefault();
        context.FaceLabels.Where(l => l.DetectedFaceId == detectedFaceId).ExecuteDelete();
        context.FaceLabels.Add(new FaceLabel
        {
            DetectedFaceId = detectedFaceId,
            PersonId = personId,
            Confirmed = confirmed,
            Source = source,
            Confidence = confidence,
        });
        context.SaveChanges();
        PruneIfOrphanedManualPerson(context, previousPersonId);
    }

    public void DeleteFaceLabel(long detectedFaceId)
    {
        using var context = NewContext();
        long? personId = context.FaceLabels
            .Where(l => l.DetectedFaceId == detectedFaceId)
            .Select(l => l.PersonId)
            .FirstOrDefault();
        context.FaceLabels.Where(l => l.DetectedFaceId == detectedFaceId).ExecuteDelete();
        PruneIfOrphanedManualPerson(context, personId);
    }

    /// <summary>Permanently removes a detected face box (and any label on it) - used to
    /// discard a manually-drawn box the user backed out of tagging, or to correct a
    /// wrongly-placed box (manual or auto-detected).</summary>
    public void DeleteDetectedFace(long detectedFaceId)
    {
        using var context = NewContext();
        long? personId = context.FaceLabels
            .Where(l => l.DetectedFaceId == detectedFaceId)
            .Select(l => l.PersonId)
            .FirstOrDefault();
        context.FaceLabels.Where(l => l.DetectedFaceId == detectedFaceId).ExecuteDelete();
        context.DetectedFaces.Where(f => f.Id == detectedFaceId).ExecuteDelete();
        PruneIfOrphanedManualPerson(context, personId);
    }

    /// <summary>
    /// A manually-created person (no VrcUserId) has no external identity to fall back on -
    /// once their last confirmed face tag is removed or reassigned elsewhere, the entry is
    /// just dead clutter in the player-filter dropdown, so it's deleted outright. A VRCX-linked
    /// person is left alone even at zero current tags - they have real external identity (and
    /// possibly a cached profile thumbnail) worth keeping for the next time they're tagged.
    /// </summary>
    private static void PruneIfOrphanedManualPerson(VrcdnDbContext context, long? personId)
    {
        if (personId is not long id) return;
        bool stillTagged = context.FaceLabels.Any(l => l.Confirmed && l.PersonId == id);
        if (stillTagged) return;

        var person = context.RegisteredPeople.FirstOrDefault(p => p.Id == id);
        if (person is not null && person.VrcUserId is null)
        {
            context.RegisteredPeople.Remove(person);
            context.SaveChanges();
        }
    }

    public RegisteredPerson FindOrCreatePersonByVrcUserId(string vrcUserId, string displayName)
    {
        using var context = NewContext();
        var existing = context.RegisteredPeople.FirstOrDefault(p => p.VrcUserId == vrcUserId);
        if (existing is not null) return existing;

        var person = new RegisteredPerson { Name = displayName, VrcUserId = vrcUserId };
        context.RegisteredPeople.Add(person);
        context.SaveChanges();
        return person;
    }

    public RegisteredPerson CreatePerson(string name)
    {
        using var context = NewContext();
        var person = new RegisteredPerson { Name = name };
        context.RegisteredPeople.Add(person);
        context.SaveChanges();
        return person;
    }

    /// <summary>Corrects a person's display name in place (e.g. a typo like "saya" ->
    /// "sayakiss") - existing FaceLabels keep pointing at the same PersonId, so every photo
    /// already tagged with them picks up the corrected name automatically.</summary>
    public void RenamePerson(long personId, string newName)
    {
        using var context = NewContext();
        context.RegisteredPeople.Where(p => p.Id == personId)
            .ExecuteUpdate(s => s.SetProperty(p => p.Name, newName));
    }

    public List<RegisteredPerson> GetAllPersons()
    {
        using var context = NewContext();
        return context.RegisteredPeople.AsNoTracking().OrderBy(p => p.Name).ToList();
    }

    /// <summary>
    /// Every VrcUserId with at least one confirmed visual face tag anywhere in the library -
    /// drives the player-filter dropdown's "(tagged)" annotation.
    /// </summary>
    public HashSet<string> GetTaggedUserIds()
    {
        using var context = NewContext();
        return context.FaceLabels.AsNoTracking()
            .Where(l => l.Confirmed && l.PersonId != null)
            .Join(context.RegisteredPeople, l => l.PersonId, p => p.Id, (l, p) => p.VrcUserId)
            .Where(id => id != null)
            .Select(id => id!)
            .Distinct()
            .ToHashSet();
    }

    /// <summary>
    /// Photo ids where this specific VRC user has a confirmed visual face tag - drives the
    /// "Tagged only" checkbox filter (distinct from "this photo's VRCX metadata lists this
    /// player", which PhotoRepository.GetPhotoIdsForUser answers instead).
    /// </summary>
    public HashSet<long> GetTaggedPhotoIdsForUser(string vrcUserId)
    {
        using var context = NewContext();
        var personIds = context.RegisteredPeople.Where(p => p.VrcUserId == vrcUserId).Select(p => p.Id);
        var faceIds = context.FaceLabels
            .Where(l => l.Confirmed && l.PersonId != null && personIds.Contains(l.PersonId!.Value))
            .Select(l => l.DetectedFaceId);
        return context.DetectedFaces
            .Where(f => faceIds.Contains(f.Id))
            .Select(f => f.PhotoId)
            .ToHashSet();
    }

    /// <summary>
    /// Photo ids where this specific person has a confirmed face tag, looked up directly by
    /// PersonId - the counterpart to GetTaggedPhotoIdsForUser for manually-created people who
    /// have no VrcUserId to key off of (VRCX never observed them, so there's no "presence" set
    /// to narrow from; a manual person's filter IS the tagged-photo set).
    /// </summary>
    public HashSet<long> GetTaggedPhotoIdsForPerson(long personId)
    {
        using var context = NewContext();
        var faceIds = context.FaceLabels
            .Where(l => l.Confirmed && l.PersonId == personId)
            .Select(l => l.DetectedFaceId);
        return context.DetectedFaces
            .Where(f => faceIds.Contains(f.Id))
            .Select(f => f.PhotoId)
            .ToHashSet();
    }

    public void SetVrcProfileThumbnail(long personId, byte[] thumbnail)
    {
        using var context = NewContext();
        context.RegisteredPeople.Where(p => p.Id == personId).ExecuteUpdate(s => s
            .SetProperty(p => p.VrcProfileThumbnail, thumbnail)
            .SetProperty(p => p.VrcProfileThumbnailFetchedAt, DateTime.UtcNow));
    }

    /// <summary>
    /// Photo ids with at least one unconfirmed EmbeddingMatch suggestion at or above the given
    /// confidence - drives the main window's "Min suggestion confidence" filter slider.
    /// </summary>
    public HashSet<long> GetPhotoIdsWithSuggestionConfidenceAtLeast(float minConfidence)
    {
        using var context = NewContext();
        var faceIds = context.FaceLabels
            .Where(l => !l.Confirmed && l.Source == FaceLabelSource.EmbeddingMatch && l.Confidence >= minConfidence)
            .Select(l => l.DetectedFaceId);
        return context.DetectedFaces
            .Where(f => faceIds.Contains(f.Id))
            .Select(f => f.PhotoId)
            .ToHashSet();
    }

    public List<DetectedFace> GetDetectedFacesWithoutEmbedding()
    {
        using var context = NewContext();
        return context.DetectedFaces.AsNoTracking().Where(f => f.Embedding == null).ToList();
    }

    public void SetEmbedding(long detectedFaceId, byte[] embedding)
    {
        using var context = NewContext();
        context.DetectedFaces.Where(f => f.Id == detectedFaceId)
            .ExecuteUpdate(s => s.SetProperty(f => f.Embedding, embedding));
    }

    /// <summary>
    /// Embeddings of every confirmed FaceLabel pointing to this person - the "already
    /// manually-tagged face crops" half of their reference material (the other half, the VRCX
    /// profile picture, comes from RegisteredPerson.VrcProfileThumbnail directly and is
    /// embedded separately by the caller).
    /// </summary>
    public List<byte[]> GetReferenceEmbeddingsForPerson(long personId)
    {
        using var context = NewContext();
        return context.FaceLabels
            .Where(l => l.Confirmed && l.PersonId == personId)
            .Join(context.DetectedFaces, l => l.DetectedFaceId, f => f.Id, (l, f) => f.Embedding)
            .Where(e => e != null)
            .Select(e => e!)
            .ToList();
    }

    /// <summary>
    /// Faces eligible for a new suggestion: have an embedding already computed, and either no
    /// label at all, or only an unconfirmed EmbeddingMatch label (safe to re-score and replace
    /// as more reference data accumulates - never touches a confirmed label, or any label from
    /// a source other than EmbeddingMatch).
    /// </summary>
    public List<DetectedFace> GetFacesNeedingSuggestion()
    {
        using var context = NewContext();
        var settledFaceIds = context.FaceLabels
            .Where(l => l.Confirmed || l.Source != FaceLabelSource.EmbeddingMatch)
            .Select(l => l.DetectedFaceId);
        return context.DetectedFaces.AsNoTracking()
            .Where(f => f.Embedding != null && !settledFaceIds.Contains(f.Id))
            .ToList();
    }
}

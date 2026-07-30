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

    public Dictionary<long, int> GetFaceCountsByPhoto()
    {
        using var context = NewContext();
        return context.DetectedFaces
            .GroupBy(f => f.PhotoId)
            .Select(g => new { PhotoId = g.Key, Count = g.Count() })
            .ToDictionary(x => x.PhotoId, x => x.Count);
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
    }

    public void DeleteFaceLabel(long detectedFaceId)
    {
        using var context = NewContext();
        context.FaceLabels.Where(l => l.DetectedFaceId == detectedFaceId).ExecuteDelete();
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

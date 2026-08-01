using Microsoft.EntityFrameworkCore;
using VrcPhotoManager.Models;
using VrcPhotoManager.Services;

namespace VrcPhotoManager.Data;

public class FaceRepository(string dbPath)
{
    private VrcdnDbContext NewContext() => new(dbPath);

    /// <summary>
    /// Replaces this photo's previously-detected faces - but only the ones nobody has reviewed
    /// yet. A face with ANY label (confirmed tag, &lt;unknown&gt;, or an unconfirmed suggestion) is
    /// preserved untouched, whether the detector or a manually-drawn box created it: there's no
    /// IsManual flag on DetectedFace, so the old "delete everything, reinsert fresh" approach
    /// silently destroyed manual tags on every re-scan (found via a real question - "does Scan
    /// Faces create a new box for already manual tagged faces?" - the actual answer was worse:
    /// it deleted the manual box/tag outright and left the FaceLabel row dangling, since there's
    /// no FK cascade from face_labels to detected_faces). A fresh detection that overlaps a
    /// preserved face is skipped rather than inserted, so re-scanning an already-reviewed photo
    /// doesn't stack a duplicate untagged box directly on top of it.
    /// </summary>
    public void InsertDetectedFaces(long photoId, IEnumerable<FaceBox> faces)
    {
        using var context = NewContext();
        var labeledFaceIds = context.FaceLabels.Select(l => l.DetectedFaceId).ToHashSet();
        var existing = context.DetectedFaces.Where(f => f.PhotoId == photoId).ToList();
        // Deleted rows are preserved (never resurrected/overwritten) just like labeled ones -
        // deleting a box is as deliberate a review decision as marking it <unknown>.
        var preserved = existing.Where(f => labeledFaceIds.Contains(f.Id) || f.Deleted).ToList();
        context.DetectedFaces.RemoveRange(existing.Where(f => !labeledFaceIds.Contains(f.Id) && !f.Deleted));

        foreach (var f in faces)
        {
            if (preserved.Any(p => Overlaps(p, f))) continue;
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

    /// <summary>IoU (intersection over union) > 0.3 counts as "the same face" for re-scan
    /// dedup purposes - loose on purpose, since the detector rarely lands pixel-identical boxes
    /// across separate runs on the same photo.</summary>
    private static bool Overlaps(DetectedFace a, FaceBox b)
    {
        int ix1 = Math.Max(a.X, b.X), iy1 = Math.Max(a.Y, b.Y);
        int ix2 = Math.Min(a.X + a.Width, b.X + b.Width), iy2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        int iw = Math.Max(0, ix2 - ix1), ih = Math.Max(0, iy2 - iy1);
        int intersection = iw * ih;
        if (intersection == 0) return false;
        int union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union > 0 && (double)intersection / union > 0.3;
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
            .Where(f => !f.Deleted && !notAFaceIds.Contains(f.Id))
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
        return context.DetectedFaces.AsNoTracking()
            .Where(f => f.PhotoId == photoId && !f.Deleted).OrderBy(f => f.Id).ToList();
    }

    public Dictionary<long, FaceLabel> GetFaceLabelsByPhoto(long photoId)
    {
        using var context = NewContext();
        var faceIds = context.DetectedFaces.Where(f => f.PhotoId == photoId && !f.Deleted).Select(f => f.Id);
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

    /// <summary>
    /// Soft-deletes a detected face box - used to discard a manually-drawn box the user backed
    /// out of tagging, or to dismiss a wrongly-placed/false-positive box (manual or
    /// auto-detected). The row (and any label on it) is kept, not removed: a dismissed
    /// false-positive detection is exactly the labeled data needed to later review or tune
    /// detection quality (e.g. "these were all too small to be real faces"), and every read
    /// query in this class already excludes Deleted rows, so it's invisible everywhere it
    /// matters without losing the data.
    /// </summary>
    public void DeleteDetectedFace(long detectedFaceId)
    {
        using var context = NewContext();
        long? personId = context.FaceLabels
            .Where(l => l.DetectedFaceId == detectedFaceId)
            .Select(l => l.PersonId)
            .FirstOrDefault();
        context.DetectedFaces.Where(f => f.Id == detectedFaceId)
            .ExecuteUpdate(s => s.SetProperty(f => f.Deleted, true));
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
        // A label surviving only on a now-deleted face box doesn't count as "still tagged" -
        // deleting the box is equivalent to un-identifying the person in that photo.
        var activeFaceIds = context.DetectedFaces.Where(f => !f.Deleted).Select(f => f.Id);
        bool stillTagged = context.FaceLabels
            .Any(l => l.Confirmed && l.PersonId == id && activeFaceIds.Contains(l.DetectedFaceId));
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
    /// The most recently confirmed-tagged people, newest first and deduped - powers the Tag
    /// Faces picker's quick shortlist, so frequently-tagged people don't require typing or
    /// scrolling a list that only grows with every person ever registered (that used to be the
    /// whole "registered people" section - see OpenPicker in TagFacesWindow.xaml.cs).
    /// Deduping happens client-side (not via SQL DISTINCT) so "most recent occurrence wins" is
    /// guaranteed rather than left to an unspecified SQL tie-break; the initial 200-row cap
    /// keeps that in-memory work bounded while comfortably covering enough history to find
    /// `limit` distinct people in practice.
    /// </summary>
    public List<RegisteredPerson> GetRecentlyTaggedPersons(int limit = 10)
    {
        using var context = NewContext();
        var recentPersonIds = context.FaceLabels
            .Where(l => l.Confirmed && l.PersonId != null)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => l.PersonId!.Value)
            .Take(200)
            .AsEnumerable()
            .Distinct()
            .Take(limit)
            .ToList();

        var personsById = context.RegisteredPeople
            .Where(p => recentPersonIds.Contains(p.Id))
            .ToDictionary(p => p.Id);
        return recentPersonIds.Where(personsById.ContainsKey).Select(id => personsById[id]).ToList();
    }

    /// <summary>
    /// Inserts or refreshes local, permanent records of VRC users seen via VRCX (friends list
    /// or gamelog) - see KnownVrcUser for why this exists (VRCX's own data can be cleared or a
    /// friend removed, which would otherwise silently regress the Tag Faces autocomplete).
    /// Called opportunistically every time Tag Faces opens with whatever VRCX returned that
    /// session, so the cache only ever grows/refreshes, never shrinks on its own.
    /// </summary>
    public void UpsertKnownVrcUsers(IEnumerable<(string UserId, string DisplayName)> users)
    {
        using var context = NewContext();
        // Dedupe by UserId first - the same person can legitimately appear in both the friends
        // list and gamelog-seen results (the common case: you've played with most of your
        // friends), and Add()-ing the same not-yet-known UserId twice in one batch throws
        // "cannot be tracked because another instance with the same key value ... is already
        // being tracked" (found via a real crash report).
        var usersList = users.GroupBy(u => u.UserId).Select(g => g.First()).ToList();
        var incomingIds = usersList.Select(u => u.UserId).ToHashSet();
        var existing = context.KnownVrcUsers.Where(u => incomingIds.Contains(u.UserId)).ToDictionary(u => u.UserId);

        var now = DateTime.UtcNow;
        foreach (var (userId, displayName) in usersList)
        {
            if (existing.TryGetValue(userId, out var row))
            {
                row.DisplayName = displayName;
                row.LastSeenAt = now;
            }
            else
            {
                context.KnownVrcUsers.Add(new KnownVrcUser { UserId = userId, DisplayName = displayName, LastSeenAt = now });
            }
        }
        context.SaveChanges();
    }

    public List<(string UserId, string DisplayName)> GetKnownVrcUsers()
    {
        using var context = NewContext();
        return context.KnownVrcUsers.AsNoTracking()
            .Select(u => new { u.UserId, u.DisplayName })
            .AsEnumerable()
            .Select(u => (u.UserId, u.DisplayName))
            .ToList();
    }

    /// <summary>Cap enforced by AddOrCaptureAlias/CaptureAliasesFromHistory - see
    /// VrcUserAlias for why Manual entries are never evicted but History ones are.</summary>
    private const int MaxAliasesPerUser = 6;

    public List<VrcUserAlias> GetAliasesForUser(string userId)
    {
        using var context = NewContext();
        return context.VrcUserAliases.AsNoTracking().Where(a => a.UserId == userId).OrderBy(a => a.Alias).ToList();
    }

    /// <summary>All aliases, grouped by user id - loaded once per Tag Faces open (like
    /// _friends/_gamelogSeenPlayers/_knownVrcUsers) so every keystroke in the search box can
    /// check aliases without a DB round trip.</summary>
    public Dictionary<string, List<string>> GetAllAliasesGroupedByUser()
    {
        using var context = NewContext();
        return context.VrcUserAliases.AsNoTracking()
            .Select(a => new { a.UserId, a.Alias })
            .AsEnumerable()
            .GroupBy(a => a.UserId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Alias).ToList());
    }

    /// <summary>
    /// Adds one alias (the manual "+" button path) - honors the per-user cap: at
    /// MaxAliasesPerUser, evicts the oldest History-sourced alias to make room (Manual
    /// entries are never evicted). If already at cap with nothing evictable (e.g. 6 manual
    /// entries already), the add is silently skipped rather than surfaced as an error - a
    /// rare edge case not worth a dedicated UI path. No-ops if this exact (UserId, Alias)
    /// pair is already recorded.
    /// </summary>
    public void AddAlias(string userId, string alias)
    {
        using var context = NewContext();
        var existing = context.VrcUserAliases.Where(a => a.UserId == userId).OrderBy(a => a.AddedAt).ToList();
        if (existing.Any(a => a.Alias == alias)) return;

        if (existing.Count >= MaxAliasesPerUser)
        {
            var oldestHistory = existing.FirstOrDefault(a => a.Source == VrcUserAliasSource.History);
            if (oldestHistory is null) return;
            context.VrcUserAliases.Remove(oldestHistory);
        }

        context.VrcUserAliases.Add(new VrcUserAlias { UserId = userId, Alias = alias, Source = VrcUserAliasSource.Manual });
        context.SaveChanges();
    }

    public void RemoveAlias(long aliasId)
    {
        using var context = NewContext();
        context.VrcUserAliases.Where(a => a.Id == aliasId).ExecuteDelete();
    }

    /// <summary>
    /// Bulk automatic-capture pass (friend rename history + gamelog name history - see
    /// TagFacesWindow's constructor for the callers) - one query for existing aliases across
    /// every affected user, not one round trip per candidate, since this can run against
    /// hundreds of candidates every time Tag Faces opens. Same cap/eviction rule as AddAlias,
    /// applied in memory across the whole batch.
    /// </summary>
    public void CaptureAliasesFromHistory(IEnumerable<(string UserId, string Alias)> candidates)
    {
        using var context = NewContext();
        var candidateList = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Alias))
            .Distinct()
            .ToList();
        if (candidateList.Count == 0) return;

        var affectedUserIds = candidateList.Select(c => c.UserId).ToHashSet();
        var existingByUser = context.VrcUserAliases
            .Where(a => affectedUserIds.Contains(a.UserId))
            .ToList()
            .GroupBy(a => a.UserId)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.AddedAt).ToList());

        foreach (var (userId, alias) in candidateList)
        {
            if (!existingByUser.TryGetValue(userId, out var existing))
            {
                existing = [];
                existingByUser[userId] = existing;
            }
            if (existing.Any(a => a.Alias == alias)) continue;

            if (existing.Count >= MaxAliasesPerUser)
            {
                var oldestHistory = existing.FirstOrDefault(a => a.Source == VrcUserAliasSource.History);
                if (oldestHistory is null) continue;
                context.VrcUserAliases.Remove(oldestHistory);
                existing.Remove(oldestHistory);
            }

            var newAlias = new VrcUserAlias { UserId = userId, Alias = alias, Source = VrcUserAliasSource.History };
            context.VrcUserAliases.Add(newAlias);
            existing.Add(newAlias);
        }
        context.SaveChanges();
    }

    /// <summary>
    /// Every VrcUserId with at least one confirmed visual face tag anywhere in the library -
    /// drives the player-filter dropdown's "(tagged)" annotation.
    /// </summary>
    public HashSet<string> GetTaggedUserIds()
    {
        using var context = NewContext();
        var activeFaceIds = context.DetectedFaces.Where(f => !f.Deleted).Select(f => f.Id);
        return context.FaceLabels.AsNoTracking()
            .Where(l => l.Confirmed && l.PersonId != null && activeFaceIds.Contains(l.DetectedFaceId))
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
            .Where(f => !f.Deleted && faceIds.Contains(f.Id))
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
            .Where(f => !f.Deleted && faceIds.Contains(f.Id))
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
            .Where(f => !f.Deleted && faceIds.Contains(f.Id))
            .Select(f => f.PhotoId)
            .ToHashSet();
    }

    public List<DetectedFace> GetDetectedFacesWithoutEmbedding()
    {
        using var context = NewContext();
        return context.DetectedFaces.AsNoTracking().Where(f => !f.Deleted && f.Embedding == null).ToList();
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
            .Join(context.DetectedFaces.Where(f => !f.Deleted), l => l.DetectedFaceId, f => f.Id, (l, f) => f.Embedding)
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
            .Where(f => !f.Deleted && f.Embedding != null && !settledFaceIds.Contains(f.Id))
            .ToList();
    }
}

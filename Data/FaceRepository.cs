using Microsoft.EntityFrameworkCore;
using VrcPhotoManager.Models;
using VrcPhotoManager.Services;

namespace VrcPhotoManager.Data;

public class FaceRepository(string dbPath)
{
    private VrcdnDbContext NewContext() => new(dbPath);

    /// <summary>Result of InsertDetectedFaces - lets ScanFacesAsync report how much of a
    /// re-scan actually found something new versus just confirming what was already known.
    /// Existing is the count of previously-detected faces on this photo that were kept as-is
    /// (already reviewed, or a prior scan's still-unreviewed box the detector re-found and
    /// therefore didn't need to duplicate); New is the count of faces this call actually
    /// inserted as fresh DetectedFace rows; Removed is the count of previously-detected,
    /// never-reviewed boxes this call cleared because the current detection pass no longer
    /// found anything there - in practice these were false-positive boxes from an earlier,
    /// less accurate pass (a real reviewed box is never touched here, see the preserved-set
    /// logic below).</summary>
    public readonly record struct FaceInsertResult(int Existing, int New, int Removed);

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
    public FaceInsertResult InsertDetectedFaces(long photoId, IEnumerable<FaceBox> faces)
    {
        using var context = NewContext();
        var labeledFaceIds = context.FaceLabels.Select(l => l.DetectedFaceId).ToHashSet();
        var existing = context.DetectedFaces.Where(f => f.PhotoId == photoId).ToList();
        // Deleted rows are preserved (never resurrected/overwritten) just like labeled ones -
        // deleting a box is as deliberate a review decision as marking it <unknown>.
        var preserved = existing.Where(f => labeledFaceIds.Contains(f.Id) || f.Deleted).ToList();
        var stale = existing.Where(f => !labeledFaceIds.Contains(f.Id) && !f.Deleted).ToList();
        context.DetectedFaces.RemoveRange(stale);

        int inserted = 0;
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
            inserted++;
        }
        context.SaveChanges();
        return new FaceInsertResult(preserved.Count, inserted, stale.Count);
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

    /// <summary>Photo ids with at least one detected face nobody has reviewed yet - no
    /// FaceLabel row at all, and not soft-deleted (see DeleteDetectedFace/TagFacesWindow's
    /// "All tagged" button). Combined with Photo.FacesScanned (PhotoRepository), this lets
    /// Detect Faces skip re-invoking the ML detector on photos that are fully resolved: every
    /// detection already tagged, marked &lt;unknown&gt;, or deleted, so there's nothing left to
    /// find. A never-scanned photo (FacesScanned=false) is never in this set - it wouldn't have
    /// any DetectedFaces rows yet either way - so the caller must still check FacesScanned
    /// separately for that case.</summary>
    public HashSet<long> GetPhotoIdsWithUnresolvedFaces()
    {
        using var context = NewContext();
        var labeledFaceIds = context.FaceLabels.Select(l => l.DetectedFaceId).ToHashSet();
        return context.DetectedFaces
            .Where(f => !f.Deleted && !labeledFaceIds.Contains(f.Id))
            .Select(f => f.PhotoId)
            .ToHashSet();
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

    /// <summary>Writes a new suggestion log entry, or updates the existing Pending row for this
    /// face in place if one already exists - re-running Suggest Faces on an already-suggested,
    /// not-yet-reviewed face refreshes its scores instead of accumulating duplicate rows, matching
    /// how UpsertFaceLabel already replaces rather than accumulates.</summary>
    public void UpsertSuggestionLog(long detectedFaceId, long suggestedPersonId, float combinedScore,
        float faceSimilarityScore, float avatarAffinityBoost, float coOccurrenceBoost, SuggestionTier tier)
    {
        using var context = NewContext();
        var pending = context.SuggestionLogs
            .FirstOrDefault(s => s.DetectedFaceId == detectedFaceId && s.Outcome == SuggestionOutcome.Pending);
        if (pending is not null)
        {
            pending.SuggestedPersonId = suggestedPersonId;
            pending.CombinedScore = combinedScore;
            pending.FaceSimilarityScore = faceSimilarityScore;
            pending.AvatarAffinityBoost = avatarAffinityBoost;
            pending.CoOccurrenceBoost = coOccurrenceBoost;
            pending.Tier = tier;
            pending.CreatedAt = DateTime.UtcNow;
        }
        else
        {
            context.SuggestionLogs.Add(new SuggestionLog
            {
                DetectedFaceId = detectedFaceId,
                SuggestedPersonId = suggestedPersonId,
                CombinedScore = combinedScore,
                FaceSimilarityScore = faceSimilarityScore,
                AvatarAffinityBoost = avatarAffinityBoost,
                CoOccurrenceBoost = coOccurrenceBoost,
                Tier = tier,
            });
        }
        context.SaveChanges();
    }

    /// <summary>Marks the Pending suggestion log for this face (if any) as resolved - called
    /// whenever a human reviews a face in TagFacesWindow. Safe to call unconditionally on every
    /// tag-setting action: the WHERE clause makes this a no-op when the face never had a pending
    /// suggestion (e.g. it was a plain untagged box the user tagged directly).</summary>
    public void ResolveSuggestionLog(long detectedFaceId, SuggestionOutcome outcome)
    {
        using var context = NewContext();
        context.SuggestionLogs
            .Where(s => s.DetectedFaceId == detectedFaceId && s.Outcome == SuggestionOutcome.Pending)
            .ExecuteUpdate(s => s
                .SetProperty(l => l.Outcome, outcome)
                .SetProperty(l => l.OutcomeAt, DateTime.UtcNow));
    }

    /// <summary>Distinct PersonIds with a CONFIRMED face label on some other (non-deleted)
    /// detected face in the same photo - the co-occurrence boost's "who else is already
    /// confirmed here" check. Excludes the face currently being scored.</summary>
    public List<long> GetConfirmedPersonIdsInPhoto(long photoId, long excludingDetectedFaceId)
    {
        using var context = NewContext();
        var faceIdsInPhoto = context.DetectedFaces
            .Where(f => f.PhotoId == photoId && !f.Deleted && f.Id != excludingDetectedFaceId)
            .Select(f => f.Id);
        return context.FaceLabels
            .Where(l => l.Confirmed && l.PersonId != null && faceIdsInPhoto.Contains(l.DetectedFaceId))
            .Select(l => l.PersonId!.Value)
            .Distinct()
            .ToList();
    }

    /// <summary>Count of non-deleted detected faces in this photo (excluding the one currently
    /// being scored) that are still undetermined - no label at all, or only an unconfirmed
    /// EmbeddingMatch/AutoTagged suggestion. Used to enforce "co-occurrence only applies when
    /// exactly one other undetermined face remains" (this count must be zero).</summary>
    public int GetUndeterminedFaceCountInPhoto(long photoId, long excludingDetectedFaceId)
    {
        using var context = NewContext();
        var settledFaceIds = context.FaceLabels
            .Where(l => l.Confirmed)
            .Select(l => l.DetectedFaceId);
        return context.DetectedFaces
            .Count(f => f.PhotoId == photoId && !f.Deleted && f.Id != excludingDetectedFaceId
                && !settledFaceIds.Contains(f.Id));
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

    /// <summary>vrcUserId must be a real, non-empty VRC user id - never pass VRCX's own "" empty
    /// sentinel for a player it couldn't resolve (see TagFacesWindow.NormalizeVrcUserId's doc
    /// comment). A real, reproduced bug: passing "" through here made this method find (not
    /// create) whichever OTHER unrelated unresolved person happened to be created first, since
    /// EVERY unresolved player shares that same empty string - silently merging different real
    /// people's face tags onto one shared "person" record. Throwing here catches any future
    /// caller that reintroduces that mistake immediately, instead of corrupting data silently.</summary>
    public RegisteredPerson FindOrCreatePersonByVrcUserId(string vrcUserId, string displayName)
    {
        if (string.IsNullOrEmpty(vrcUserId))
        {
            throw new ArgumentException("vrcUserId must be a real, non-empty VRC user id.", nameof(vrcUserId));
        }

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
    /// Originally called automatically every time Tag Faces opened; moved to an explicit "Sync
    /// VRC Players" action (MainViewModel.SyncVrcPlayerDataAsync) after a real slowness report
    /// traced most of a ~1s Tag Faces open time to VRCX's own gamelog table, which only grows
    /// and has no natural bound - Tag Faces now just reads whatever this cache already has
    /// (GetKnownVrcUsers) instead of paying to refresh it on every single open.
    ///
    /// Also collapses what used to be two separate calls (an upsert followed by a full re-
    /// read) into one - that pair measured ~785ms combined on a real ~8300-row cache. The
    /// upsert half used to filter existing rows via `WHERE UserId IN (...)` against the
    /// incoming batch - fine at dozens of ids, but this account's gamelog-seen set alone runs
    /// into the thousands, and EF Core/SQLite do not handle an IN-clause that large well.
    /// Reading the whole table once (no IN-clause at all) is both the fix and, since the
    /// second call immediately re-read the same table right after anyway, a free elimination
    /// of a fully redundant second full scan. A matched existing row is only actually written
    /// when its DisplayName changed (a real rename) - LastSeenAt used to be unconditionally
    /// stamped to "now" on every match, and since "now" is by definition always different from
    /// whatever was stored, that guaranteed a real UPDATE for every one of the ~8300 rows on
    /// every call, even though nothing anywhere in the app actually reads LastSeenAt back.
    /// </summary>
    public List<(string UserId, string DisplayName)> UpsertKnownVrcUsersAndGetAll(IEnumerable<(string UserId, string DisplayName)> users)
    {
        using var context = NewContext();
        // Dedupe by UserId first - the same person can legitimately appear in both the friends
        // list and gamelog-seen results (the common case: you've played with most of your
        // friends), and Add()-ing the same not-yet-known UserId twice in one batch throws
        // "cannot be tracked because another instance with the same key value ... is already
        // being tracked" (found via a real crash report).
        var usersList = users.GroupBy(u => u.UserId).Select(g => g.First()).ToList();
        var existing = context.KnownVrcUsers.ToDictionary(u => u.UserId);

        foreach (var (userId, displayName) in usersList)
        {
            if (existing.TryGetValue(userId, out var row))
            {
                if (row.DisplayName != displayName) row.DisplayName = displayName;
            }
            else
            {
                var newRow = new KnownVrcUser { UserId = userId, DisplayName = displayName, LastSeenAt = DateTime.UtcNow };
                context.KnownVrcUsers.Add(newRow);
                existing[userId] = newRow;
            }
        }
        context.SaveChanges();
        return existing.Values.Select(u => (u.UserId, u.DisplayName)).ToList();
    }

    /// <summary>Local-only read of the permanent known-VRC-user cache - no VRCX query, no
    /// upsert, just whatever's already in our own (small, fast) SQLite file. What Tag Faces
    /// itself now reads on every open; UpsertKnownVrcUsersAndGetAll is only for the explicit
    /// sync action that actually refreshes this cache from VRCX.</summary>
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
    /// _friends/_knownVrcUsers) so every keystroke in the search box can check aliases without
    /// a DB round trip.</summary>
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
    /// used by App.xaml.cs's startup diagnostic self-test. GetTaggedUserTagCounts below is what
    /// actually drives the player-filter dropdown's annotation now.
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

    /// <summary>Confirmed visual face tag count per VrcUserId, anywhere in the library - drives
    /// the player-filter dropdown's "(N)" annotation (e.g. "MisoNyah (12)"), so it reads as how
    /// much tagged data exists for that person rather than just a flat yes/no "(tagged)".</summary>
    public Dictionary<string, int> GetTaggedUserTagCounts()
    {
        using var context = NewContext();
        var activeFaceIds = context.DetectedFaces.Where(f => !f.Deleted).Select(f => f.Id);
        return context.FaceLabels.AsNoTracking()
            .Where(l => l.Confirmed && l.PersonId != null && activeFaceIds.Contains(l.DetectedFaceId))
            .Join(context.RegisteredPeople, l => l.PersonId, p => p.Id, (l, p) => p.VrcUserId)
            .Where(id => id != null)
            .GroupBy(id => id!)
            .ToDictionary(g => g.Key, g => g.Count());
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
    /// Photo ids with at least one unconfirmed EmbeddingMatch or AutoTagged suggestion at or
    /// above the given confidence - drives the main window's "Min suggestion confidence" filter
    /// slider.
    /// </summary>
    public HashSet<long> GetPhotoIdsWithSuggestionConfidenceAtLeast(float minConfidence)
    {
        using var context = NewContext();
        var faceIds = context.FaceLabels
            .Where(l => !l.Confirmed && (l.Source == FaceLabelSource.EmbeddingMatch || l.Source == FaceLabelSource.AutoTagged
                || l.Source == FaceLabelSource.ExifElimination) && l.Confidence >= minConfidence)
            .Select(l => l.DetectedFaceId);
        return context.DetectedFaces
            .Where(f => !f.Deleted && faceIds.Contains(f.Id))
            .Select(f => f.PhotoId)
            .ToHashSet();
    }

    /// <summary>Per-photo MAX confidence among its unconfirmed EmbeddingMatch/AutoTagged
    /// suggestions - drives the "Suggestion Confidence (Highest First)" sort option, so the
    /// suggestions most likely to be correct (and thus fastest to review/confirm) surface
    /// first. Photos with no such suggestion are simply absent (caller treats a missing key as
    /// lowest priority).</summary>
    public Dictionary<long, float> GetMaxSuggestionConfidenceByPhoto()
    {
        using var context = NewContext();
        var rows = context.FaceLabels.AsNoTracking()
            .Where(l => !l.Confirmed && (l.Source == FaceLabelSource.EmbeddingMatch || l.Source == FaceLabelSource.AutoTagged
                || l.Source == FaceLabelSource.ExifElimination))
            .Join(context.DetectedFaces.Where(f => !f.Deleted), l => l.DetectedFaceId, f => f.Id,
                (l, f) => new { f.PhotoId, l.Confidence })
            .ToList();
        return rows.GroupBy(r => r.PhotoId).ToDictionary(g => g.Key, g => g.Max(r => r.Confidence));
    }

    /// <summary>Per-photo "most face-tagging value" signal for the "Most Tagging Value (New
    /// Info First)" sort option: the LOWEST current confirmed-reference-count among the people
    /// already suggested for this photo's still-undetermined faces, or -1 if at least one of
    /// those faces has no suggestion at all (brand new/unregistered - ranks ahead of anyone
    /// already registered, since tagging it is pure new information rather than reinforcing an
    /// existing centroid). Photos with nothing left undetermined are simply absent. Confirming a
    /// face for a thinly-referenced person improves THEIR future suggestion quality far more
    /// than another confirm for someone already well-represented - see FaceMatcher's
    /// MinReferencesForTrimming/nearest-neighbor design, where a person needs 6+ references
    /// before nearest-neighbor scoring (the more accurate mode) even applies to them at all.
    /// Lower value = more valuable; caller sorts ascending.</summary>
    public Dictionary<long, int> GetPhotoTaggingValueScores()
    {
        using var context = NewContext();

        var refCountByPerson = context.FaceLabels.AsNoTracking()
            .Where(l => l.Confirmed && l.PersonId != null)
            .GroupBy(l => l.PersonId!.Value)
            .Select(g => new { PersonId = g.Key, Count = g.Count() })
            .ToList()
            .ToDictionary(x => x.PersonId, x => x.Count);

        var labelByFaceId = context.FaceLabels.AsNoTracking()
            .Select(l => new { l.DetectedFaceId, l.Confirmed, l.Source, l.PersonId })
            .ToList()
            .ToDictionary(l => l.DetectedFaceId);

        var faces = context.DetectedFaces.AsNoTracking()
            .Where(f => !f.Deleted)
            .Select(f => new { f.Id, f.PhotoId })
            .ToList();

        var result = new Dictionary<long, int>();
        foreach (var face in faces)
        {
            labelByFaceId.TryGetValue(face.Id, out var label);
            bool undetermined = label is null
                || (!label.Confirmed && (label.Source == FaceLabelSource.EmbeddingMatch || label.Source == FaceLabelSource.AutoTagged
                    || label.Source == FaceLabelSource.ExifElimination));
            if (!undetermined) continue;

            int value = label?.PersonId is long personId && refCountByPerson.TryGetValue(personId, out int count)
                ? count
                : -1;

            if (!result.TryGetValue(face.PhotoId, out int existing) || value < existing)
            {
                result[face.PhotoId] = value;
            }
        }
        return result;
    }

    /// <summary>
    /// Links a manually-created person (no VrcUserId) to a real VRC account - powers Tag
    /// Faces' "this looks like someone VRCX already knows" merge prompt (found via a real
    /// report: tagging someone by typed name before their VRC identity was known, then VRCX
    /// search later turning up the same person under their real account, left two separate
    /// person rows - "Lumiichu" and "Lumiichu (manual)" - for the same human). If nobody else
    /// is linked to that VrcUserId yet, this is a simple relink: the manual person's existing
    /// FaceLabels/PersonReferencePhotos need no changes since they still point at the same
    /// PersonId. If another RegisteredPerson row is ALREADY linked to that VrcUserId (a true
    /// duplicate), everything pointing at the manual person is reassigned onto the existing
    /// linked person instead, and the now-empty manual row is deleted.
    /// </summary>
    public void LinkManualPersonToVrcUser(long manualPersonId, string vrcUserId)
    {
        using var context = NewContext();
        var manualPerson = context.RegisteredPeople.FirstOrDefault(p => p.Id == manualPersonId);
        if (manualPerson is null || manualPerson.VrcUserId is not null) return;

        var existingLinked = context.RegisteredPeople.FirstOrDefault(p => p.VrcUserId == vrcUserId);
        if (existingLinked is null)
        {
            manualPerson.VrcUserId = vrcUserId;
            context.SaveChanges();
            return;
        }

        context.FaceLabels.Where(l => l.PersonId == manualPersonId)
            .ExecuteUpdate(s => s.SetProperty(l => l.PersonId, existingLinked.Id));

        // A (PersonId, PhotoId) pair can already exist on the target person - reassigning
        // every row as-is would violate that unique index, so a colliding manual reference
        // photo is just dropped (the target already has one for that photo) rather than
        // reassigned.
        var targetPhotoIds = context.PersonReferencePhotos
            .Where(r => r.PersonId == existingLinked.Id).Select(r => r.PhotoId).ToHashSet();
        foreach (var r in context.PersonReferencePhotos.Where(r => r.PersonId == manualPersonId).ToList())
        {
            if (targetPhotoIds.Contains(r.PhotoId)) context.PersonReferencePhotos.Remove(r);
            else r.PersonId = existingLinked.Id;
        }

        context.RegisteredPeople.Remove(manualPerson);
        context.SaveChanges();
    }

    /// <summary>photoIds null (the default) means the whole library - used by the full-library
    /// Suggest Faces run. A non-null set scopes this to just those photos - used by Tag Faces'
    /// incremental "refresh suggestions for what's currently in view" banner, so it doesn't pay
    /// for a full-library scan just to catch up a filtered handful of photos.</summary>
    public List<DetectedFace> GetDetectedFacesWithoutEmbedding(IReadOnlyCollection<long>? photoIds = null)
    {
        using var context = NewContext();
        var query = context.DetectedFaces.AsNoTracking().Where(f => !f.Deleted && f.Embedding == null);
        if (photoIds is not null) query = query.Where(f => photoIds.Contains(f.PhotoId));
        return query.ToList();
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
    /// label at all, or only an unconfirmed EmbeddingMatch/AutoTagged/ExifElimination label (safe
    /// to re-score and replace as more reference data accumulates - never touches a confirmed
    /// label, or any label from a source other than those three). photoIds null (the default)
    /// means the whole library - see GetDetectedFacesWithoutEmbedding's doc comment for why a
    /// non-null scope exists.
    /// </summary>
    public List<DetectedFace> GetFacesNeedingSuggestion(IReadOnlyCollection<long>? photoIds = null)
    {
        using var context = NewContext();
        var settledFaceIds = context.FaceLabels
            .Where(l => l.Confirmed || (l.Source != FaceLabelSource.EmbeddingMatch && l.Source != FaceLabelSource.AutoTagged
                && l.Source != FaceLabelSource.ExifElimination))
            .Select(l => l.DetectedFaceId);
        var query = context.DetectedFaces.AsNoTracking()
            .Where(f => !f.Deleted && f.Embedding != null && !settledFaceIds.Contains(f.Id));
        if (photoIds is not null) query = query.Where(f => photoIds.Contains(f.PhotoId));
        return query.ToList();
    }

    /// <summary>Every detected face (not deleted) that isn't Confirmed yet - regardless of
    /// whether it has an embedding computed or already carries an unconfirmed suggestion.
    /// Broader than GetFacesNeedingSuggestion on purpose: FaceSuggestionService's VRCX-presence
    /// elimination pass needs no embedding at all to identify a face (it's a pure headcount
    /// against the photo's presence list), so restricting to embedded faces would silently miss
    /// brand-new detections. photoIds null means the whole library.</summary>
    public List<DetectedFace> GetUnconfirmedDetectedFaces(IReadOnlyCollection<long>? photoIds = null)
    {
        using var context = NewContext();
        var confirmedFaceIds = context.FaceLabels.Where(l => l.Confirmed).Select(l => l.DetectedFaceId);
        var query = context.DetectedFaces.AsNoTracking()
            .Where(f => !f.Deleted && !confirmedFaceIds.Contains(f.Id));
        if (photoIds is not null) query = query.Where(f => photoIds.Contains(f.PhotoId));
        return query.ToList();
    }
}

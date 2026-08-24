using VrcPhotoManager.Data;

namespace VrcPhotoManager.Services;

/// <summary>Result of a Suggest Faces run - see FaceSuggestionService.RunAsync's doc comment.
/// NoEligiblePeople is true when no registered person has enough reference material yet
/// (FaceMatcher.MinReferenceEmbeddings); callers should show that as its own message rather
/// than a "0 suggestions" result, since it means the run couldn't even attempt scoring.</summary>
public readonly record struct SuggestFacesResult(int Embedded, int Suggested, int EligiblePeople, int EliminationsApplied = 0, int ExifEliminations = 0)
{
    public bool NoEligiblePeople => EligiblePeople == 0;
}

/// <summary>Face-suggestion orchestration shared between MainViewModel's "Suggest Faces" button
/// and App.xaml.cs's headless `--run-suggest-faces` diagnostic hook - pulled out of
/// MainViewModel (where it originally lived inline) so the two never drift out of sync running
/// slightly different matching logic against the same database. progress, if given, is called
/// with a human-readable status string at the same points MainViewModel.StatusMessage used to
/// be set directly.</summary>
public static class FaceSuggestionService
{
    /// <summary>
    /// Embeds every not-yet-embedded detected face, then runs two independent identification
    /// passes: a VRCX-presence elimination pass (see below - no embedding involved at all), then
    /// CCIP embedding-based matching for whatever elimination didn't resolve. Builds a (possibly
    /// outlier-trimmed) reference set per registered person (confirmed-tag embeddings + VRC
    /// profile thumbnail), then scores every still-undetermined face against every reference of
    /// every eligible person - taking each person's single best (nearest-neighbor) match - and
    /// records suggestions that clear FaceMatcher's acceptance bar. See FaceMatcher.cs for the
    /// scoring design (differential margin over the runner-up, not a raw similarity, and why
    /// nearest-neighbor replaced an earlier centroid-averaging design) and MainViewModel's
    /// former inline version (before this extraction) for the original call site.
    ///
    /// scopedPhotoIds null (the default) means the whole library - the full-library "Suggest
    /// Faces" button and the `--run-suggest-faces` diagnostic hook both call it this way. A
    /// non-null set restricts embedding/matching to just those photos - see Tag Faces'
    /// incremental refresh banner (TagFacesWindow), which scopes this to whatever's currently
    /// filtered/visible in the main grid rather than paying for a full-library pass just to let
    /// a person just tagged start suggesting into a few other open photos. Candidate PEOPLE are
    /// never scoped - every eligible registered person is still considered for every face in
    /// scope, same as a full run; only which FACES get considered changes.
    ///
    /// photos null disables the elimination pass entirely (callers gate this on
    /// SettingsKeys.EnableExifElimination) - a photo with VRCX presence data (photo_players, or
    /// gamelog_inferred_players as a weaker fallback) where exactly one detected face is still
    /// unidentified and exactly one listed person is unaccounted for gets that face labeled as
    /// that person by pure elimination, no CCIP embedding needed. A photo_players match (VRCX's
    /// own native metadata) confirms directly; a gamelog_inferred_players match (present in the
    /// instance, not necessarily in frame) lands as a normal pending suggestion instead. Runs
    /// BEFORE the CCIP pass and before the "no eligible people" early-out below, since
    /// elimination needs no existing reference photos at all - it can identify someone who's
    /// never been tagged before, which is exactly the scenario a fresh library needs most.
    /// </summary>
    public static async Task<SuggestFacesResult> RunAsync(
        FaceRepository faces,
        CcipEmbeddingService ccipEmbedder,
        IReadOnlyDictionary<long, string> pathByPhotoId,
        IReadOnlyDictionary<long, string?> avatarTypeByPhotoId,
        Action<string>? progress = null,
        IReadOnlyCollection<long>? scopedPhotoIds = null,
        PhotoRepository? photos = null)
    {
        progress?.Invoke("Computing face embeddings...");
        var needingEmbedding = faces.GetDetectedFacesWithoutEmbedding(scopedPhotoIds);
        int embedded = 0;
        // See MainViewModel.ClassifyPhotosAsync for why bounded concurrency here is safe:
        // CcipEmbeddingService serializes its own session.Run() calls internally, so only the
        // CPU-bound preprocessing overlaps across threads.
        using var embedSemaphore = new SemaphoreSlim(Environment.ProcessorCount);
        var embedTasks = needingEmbedding.Select(async face =>
        {
            if (!pathByPhotoId.TryGetValue(face.PhotoId, out string? path)) return;
            await embedSemaphore.WaitAsync();
            try
            {
                float[] embedding = await Task.Run(() =>
                    ccipEmbedder.ComputeEmbedding(path, face.X, face.Y, face.Width, face.Height));
                faces.SetEmbedding(face.Id, CcipEmbeddingService.EmbeddingToBytes(embedding));
            }
            catch (Exception ex)
            {
                progress?.Invoke($"Embedding failed for face {face.Id}: {ex.Message}");
            }
            finally
            {
                embedSemaphore.Release();
            }

            embedded++;
            if (embedded % 25 == 0 || embedded == needingEmbedding.Count)
            {
                progress?.Invoke($"Computing face embeddings... {embedded}/{needingEmbedding.Count}");
            }
        });
        await Task.WhenAll(embedTasks);

        var persons = faces.GetAllPersons();
        int exifEliminations = 0;
        if (photos is not null)
        {
            progress?.Invoke("Checking VRCX-presence elimination...");
            var personById = persons.ToDictionary(p => p.Id);
            var unconfirmedFaces = faces.GetUnconfirmedDetectedFaces(scopedPhotoIds);
            foreach (var photoGroup in unconfirmedFaces.GroupBy(f => f.PhotoId))
            {
                // Airtight case only - exactly one face left to identify in this photo. More
                // than one is ambiguous (which face is which of several unaccounted people?);
                // extra/spurious faces (a misdetection) would make the headcount itself
                // unreliable, so this deliberately doesn't try to be clever about partial cases.
                if (photoGroup.Count() != 1) continue;
                var onlyFace = photoGroup.Single();

                // photo_players (VRCX's own native PlayerList metadata, captured at the exact
                // moment of the shot) is preferred and treated as trustworthy enough to confirm
                // directly; gamelog_inferred_players (present in the instance at that time, not
                // necessarily in the photographed frame) is a weaker fallback that only ever
                // produces a pending suggestion instead - see the confirmed: isNativeMetadata
                // line below.
                var nativePlayers = photos.GetPlayersForPhoto(photoGroup.Key);
                bool isNativeMetadata = nativePlayers.Count > 0;
                List<(string UserId, string DisplayName)> vrcxPeople = isNativeMetadata
                    ? nativePlayers.Select(p => (p.UserId, p.DisplayName)).ToList()
                    : photos.GetGamelogInferredPlayersForPhoto(photoGroup.Key).Select(p => (p.UserId, p.DisplayName)).ToList();
                if (vrcxPeople.Count == 0) continue;

                // excludingDetectedFaceId: 0 - a sentinel that never matches a real DetectedFace
                // id (EF Core ids start at 1) - to get every confirmed person in this photo with
                // nothing excluded, not "every OTHER confirmed face" the way this method is used
                // elsewhere.
                var confirmedVrcUserIds = faces.GetConfirmedPersonIdsInPhoto(photoGroup.Key, excludingDetectedFaceId: 0)
                    .Select(pid => personById.TryGetValue(pid, out var p) ? p.VrcUserId : null)
                    .Where(id => id is not null)
                    .ToHashSet();
                var unaccounted = vrcxPeople.Where(p => !confirmedVrcUserIds.Contains(p.UserId)).ToList();
                if (unaccounted.Count != 1) continue; // still ambiguous - more than one candidate left

                var (vrcUserId, displayName) = unaccounted[0];
                var person = faces.FindOrCreatePersonByVrcUserId(vrcUserId, displayName);
                faces.UpsertFaceLabel(onlyFace.Id, person.Id, confirmed: isNativeMetadata, Models.FaceLabelSource.ExifElimination, confidence: 1.0f);
                faces.UpsertSuggestionLog(onlyFace.Id, person.Id, 1.0f, 1.0f, 0f, 0f,
                    isNativeMetadata ? Models.SuggestionTier.AutoTagged : Models.SuggestionTier.ConfirmPrompt);
                if (isNativeMetadata) faces.ResolveSuggestionLog(onlyFace.Id, Models.SuggestionOutcome.ConfirmedAsIs);
                exifEliminations++;
                // Keeps personById correct in-memory (a brand-new person from
                // FindOrCreatePersonByVrcUserId wouldn't otherwise be in it) without a DB
                // round-trip per elimination - persons itself gets one single refresh after the
                // loop below, for the CCIP reference-building pass that follows.
                personById[person.Id] = person;
            }
            if (exifEliminations > 0) persons = faces.GetAllPersons();
        }

        progress?.Invoke("Building reference sets...");
        var personRefs = new Dictionary<long, List<float[]>>();
        var confirmedPhotoIdsByPerson = new Dictionary<long, HashSet<long>>();
        foreach (var person in persons)
        {
            var refs = faces.GetReferenceEmbeddingsForPerson(person.Id)
                .Select(CcipEmbeddingService.BytesToEmbedding).ToList();
            if (person.VrcProfileThumbnail is byte[] thumb)
            {
                try { refs.Add(await Task.Run(() => ccipEmbedder.ComputeEmbeddingFromBytes(thumb))); }
                catch { /* corrupt/unreadable thumbnail - skip it, may still have enough tag-derived refs */ }
            }

            var trimmed = FaceMatcher.GetTrimmedReferences(refs);
            if (trimmed is not null)
            {
                // Nearest-neighbor (each reference scored individually - see
                // GetTrimmedReferences' doc comment) only once TrimOutliers has actually run and
                // filtered the set; below that, fall back to one averaged centroid, which is
                // safer for a small, unfiltered reference set (see ComputeCentroid's doc
                // comment for the real case - Sayakiss, 4 references - this fixed).
                if (trimmed.Count >= FaceMatcher.MinReferencesForTrimming)
                {
                    personRefs[person.Id] = trimmed;
                }
                else if (FaceMatcher.ComputeCentroid(trimmed) is float[] centroid)
                {
                    personRefs[person.Id] = [centroid];
                }
                else
                {
                    continue; // zero-vector edge case - skip this person this pass
                }
                confirmedPhotoIdsByPerson[person.Id] = faces.GetTaggedPhotoIdsForPerson(person.Id);
            }
        }

        if (personRefs.Count == 0)
        {
            return new SuggestFacesResult(embedded, 0, 0, ExifEliminations: exifEliminations);
        }

        progress?.Invoke("Matching faces against registered people...");
        var personIds = personRefs.Keys.ToList();
        // Flatten every eligible person's entries into one batch, tracking which person owns
        // each by parallel index - personRefs[id] is either that person's individual trimmed
        // references (nearest-neighbor path) or a single averaged centroid (small-reference-set
        // fallback - see the personRefs-building loop above), so this uniformly handles both:
        // a person's final score is always the max over their own entries (below), which is a
        // no-op "max of one" for the centroid case.
        var flatRefs = new List<float[]>();
        var flatRefOwner = new List<long>();
        foreach (long personId in personIds)
        {
            foreach (var reference in personRefs[personId])
            {
                flatRefs.Add(reference);
                flatRefOwner.Add(personId);
            }
        }

        var toScore = faces.GetFacesNeedingSuggestion(scopedPhotoIds).Where(f => f.Embedding is not null).ToList();

        // Score every face against every eligible person up front, before any elimination - the
        // photo-wide greedy assignment below needs each face's full ranked candidate list to
        // fairly compare confidences ACROSS faces, not just whichever face happens to be
        // processed first.
        progress?.Invoke($"Matching faces against registered people... 0/{toScore.Count}");
        int scoredCount = 0;
        var scoredByFace = new Dictionary<long, List<(long PersonId, float Score)>>();
        foreach (var face in toScore)
        {
            float[] faceEmbedding = CcipEmbeddingService.BytesToEmbedding(face.Embedding!);
            // One CCIP metric-model call per face, scored against every eligible person's
            // individual references at once - see ComputeMatchScores' doc comment for why this
            // must be batched rather than one call per reference.
            float[] refScores = await Task.Run(() => ccipEmbedder.ComputeMatchScores(faceEmbedding, flatRefs));
            scoredCount++;
            // Every face, not every 25 like the embedding loop above - a real report found this
            // loop silently sat on its last progress message ("Building reference sets...") for
            // the entire scoring pass with zero visible movement, which read as a hang even
            // though each iteration really does yield back to the UI thread via the await above -
            // an unscoped (whole-library) run can have thousands of faces here, and it's cheap to
            // just always report.
            progress?.Invoke($"Matching faces against registered people... {scoredCount}/{toScore.Count}");

            var bestPerPerson = new Dictionary<long, float>();
            for (int i = 0; i < refScores.Length; i++)
            {
                long personId = flatRefOwner[i];
                if (!bestPerPerson.TryGetValue(personId, out float existing) || refScores[i] > existing)
                {
                    bestPerPerson[personId] = refScores[i];
                }
            }
            scoredByFace[face.Id] = bestPerPerson
                .Select(kv => (PersonId: kv.Key, Score: kv.Value))
                .OrderByDescending(s => s.Score)
                .ToList();
        }

        int suggested = 0;
        int eliminationsApplied = 0;
        foreach (var photoGroup in toScore.GroupBy(f => f.PhotoId))
        {
            var facesInPhoto = photoGroup.ToList();
            var facesById = facesInPhoto.ToDictionary(f => f.Id);

            // Elimination: a person already confirmed on a DIFFERENT face in this same photo
            // can't also be the person in THIS face (one photographed face = one identity).
            // Same set regardless of which specific face id is passed as "excluding" here -
            // none of facesInPhoto are themselves confirmed (GetFacesNeedingSuggestion already
            // excludes those), so the result only depends on OTHER, already-confirmed faces.
            var claimed = new HashSet<long>(faces.GetConfirmedPersonIdsInPhoto(photoGroup.Key, facesInPhoto[0].Id));

            // Greedy per-photo assignment: repeatedly pick whichever remaining face has the
            // single highest-scoring still-unclaimed candidate anywhere in the photo, so the
            // most confident match gets first claim on a person instead of whichever face
            // happened to be processed first. A real, reproduced problem this fixes: two
            // different unconfirmed faces in the same photo both getting suggested as the SAME
            // person in one run, when only one of them could possibly be right. Only claims a
            // person once a suggestion for them is actually ACCEPTED (see below) - a candidate
            // that didn't clear the acceptance bar for one face must stay available for another.
            var remaining = new HashSet<long>(facesInPhoto.Select(f => f.Id));
            while (remaining.Count > 0)
            {
                long? pickFaceId = null;
                (long PersonId, float Score) pickCandidate = default;
                float bestScore = float.NegativeInfinity;
                foreach (long faceId in remaining)
                {
                    var topAvailable = scoredByFace[faceId].FirstOrDefault(c => !claimed.Contains(c.PersonId));
                    // Default tuple's PersonId is 0 - never a real RegisteredPerson id (EF Core
                    // ids start at 1) - used purely as this face's "nothing left available"
                    // sentinel, never written to the db.
                    if (topAvailable.PersonId == 0) continue;
                    if (topAvailable.Score > bestScore)
                    {
                        bestScore = topAvailable.Score;
                        pickFaceId = faceId;
                        pickCandidate = topAvailable;
                    }
                }
                if (pickFaceId is null) break; // nobody left has any unclaimed candidate at all
                remaining.Remove(pickFaceId.Value);

                var face = facesById[pickFaceId.Value];
                var fullRanked = scoredByFace[pickFaceId.Value];
                var scored = fullRanked.Where(c => !claimed.Contains(c.PersonId)).ToList();
                if (scored.Count < fullRanked.Count) eliminationsApplied++;

                // pickCandidate is scored[0] recomputed (claimed hasn't changed since the scan
                // above picked it) - reused directly as `best` rather than redundantly re-reading
                // scored[0].
                var best = pickCandidate;
                bool accept;
                float confidence;
                if (scored.Count == 1)
                {
                    accept = best.Score >= FaceMatcher.SingleCandidateThreshold;
                    confidence = best.Score;
                }
                else
                {
                    float margin = best.Score - scored[1].Score;
                    accept = margin >= FaceMatcher.DifferentialMarginThreshold;
                    confidence = margin;
                }

                if (!accept)
                {
                    // A real, reproduced bug: this face had a PRIOR unconfirmed suggestion (or it
                    // wouldn't be in toScore's EmbeddingMatch/AutoTagged set), and re-scoring just
                    // concluded nothing here clears the acceptance bar anymore - e.g. its best
                    // candidate got legitimately eliminated by a stronger, better-scoring face
                    // elsewhere in the same photo. Leaving the stale old label in place would
                    // silently keep showing a suggestion the algorithm no longer stands behind.
                    // DeleteFaceLabel is a safe no-op when there was no prior label at all (a
                    // brand-new face that never had one).
                    faces.DeleteFaceLabel(face.Id);
                    continue;
                }

                // Avatar-affinity boost: does this photo's AvatarType appear anywhere in the best
                // candidate's own confirmed photos? No confident AvatarType on this photo, or no overlap,
                // means zero boost - never a penalty (see Global Constraints).
                float avatarAffinityBoost = 0f;
                if (avatarTypeByPhotoId.TryGetValue(face.PhotoId, out string? thisPhotoAvatarType)
                    && thisPhotoAvatarType is not null
                    && confirmedPhotoIdsByPerson.TryGetValue(best.PersonId, out var bestPersonPhotoIds)
                    && bestPersonPhotoIds.Any(pid => avatarTypeByPhotoId.TryGetValue(pid, out string? knownType) && knownType == thisPhotoAvatarType))
                {
                    avatarAffinityBoost = FaceMatcher.AvatarAffinityBoost;
                }

                // Co-occurrence boost: exactly one other person already confirmed in this photo, zero
                // other undetermined faces remaining, and that pair has been confirmed together enough
                // times before to trust it as a real pattern rather than one coincidental photo.
                float coOccurrenceBoost = 0f;
                if (faces.GetUndeterminedFaceCountInPhoto(face.PhotoId, face.Id) == 0)
                {
                    var otherConfirmedPersonIds = faces.GetConfirmedPersonIdsInPhoto(face.PhotoId, face.Id);
                    if (otherConfirmedPersonIds.Count == 1
                        && confirmedPhotoIdsByPerson.TryGetValue(best.PersonId, out var bestIds)
                        && confirmedPhotoIdsByPerson.TryGetValue(otherConfirmedPersonIds[0], out var otherIds)
                        && bestIds.Intersect(otherIds).Count() >= FaceMatcher.MinCoOccurrenceCount)
                    {
                        coOccurrenceBoost = FaceMatcher.CoOccurrenceBoost;
                    }
                }

                float combinedScore = confidence + avatarAffinityBoost + coOccurrenceBoost;
                // AutoTagThreshold is calibrated against the margin scale (DifferentialMarginThreshold-based);
                // a single-candidate raw match score (SingleCandidateThreshold, CCIP's own -0.178475
                // same-character cutoff) is a fundamentally different signal - "clears the model's own
                // same-character bar" isn't the same claim as "clearly beats every other registered
                // person" - so single-candidate suggestions are capped at ConfirmPrompt regardless of
                // score, not just numerically incapable of reaching AutoTagThreshold.
                Models.SuggestionTier tier = scored.Count > 1 ? FaceMatcher.DetermineTier(combinedScore) : Models.SuggestionTier.ConfirmPrompt;
                Models.FaceLabelSource source = tier == Models.SuggestionTier.AutoTagged ? Models.FaceLabelSource.AutoTagged : Models.FaceLabelSource.EmbeddingMatch;

                faces.UpsertFaceLabel(face.Id, best.PersonId, confirmed: false, source, combinedScore);
                faces.UpsertSuggestionLog(face.Id, best.PersonId, combinedScore, confidence, avatarAffinityBoost, coOccurrenceBoost, tier);
                suggested++;
                claimed.Add(best.PersonId);
            }
        }

        return new SuggestFacesResult(embedded, suggested, personRefs.Count, eliminationsApplied, exifEliminations);
    }
}

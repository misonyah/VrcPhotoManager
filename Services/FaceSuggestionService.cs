using VrcPhotoManager.Data;

namespace VrcPhotoManager.Services;

/// <summary>Result of a Suggest Faces run - see FaceSuggestionService.RunAsync's doc comment.
/// NoEligiblePeople is true when no registered person has enough reference material yet
/// (FaceMatcher.MinReferenceEmbeddings); callers should show that as its own message rather
/// than a "0 suggestions" result, since it means the run couldn't even attempt scoring.</summary>
public readonly record struct SuggestFacesResult(int Embedded, int Suggested, int EligiblePeople, int EliminationsApplied = 0)
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
    /// Embeds every not-yet-embedded detected face, builds a (possibly outlier-trimmed)
    /// reference set per registered person (confirmed-tag embeddings + VRC profile thumbnail),
    /// then scores every still-undetermined face against every reference of every eligible
    /// person - taking each person's single best (nearest-neighbor) match - and records
    /// suggestions that clear FaceMatcher's acceptance bar. See FaceMatcher.cs for the scoring
    /// design (differential margin over the runner-up, not a raw similarity, and why
    /// nearest-neighbor replaced an earlier centroid-averaging design) and MainViewModel's
    /// former inline version (before this extraction) for the original call site.
    /// </summary>
    public static async Task<SuggestFacesResult> RunAsync(
        FaceRepository faces,
        CcipEmbeddingService ccipEmbedder,
        IReadOnlyDictionary<long, string> pathByPhotoId,
        IReadOnlyDictionary<long, string?> avatarTypeByPhotoId,
        Action<string>? progress = null)
    {
        progress?.Invoke("Computing face embeddings...");
        var needingEmbedding = faces.GetDetectedFacesWithoutEmbedding();
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

        progress?.Invoke("Building reference sets...");
        var persons = faces.GetAllPersons();
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
            return new SuggestFacesResult(embedded, 0, 0);
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

        var toScore = faces.GetFacesNeedingSuggestion().Where(f => f.Embedding is not null).ToList();

        // Score every face against every eligible person up front, before any elimination - the
        // photo-wide greedy assignment below needs each face's full ranked candidate list to
        // fairly compare confidences ACROSS faces, not just whichever face happens to be
        // processed first.
        var scoredByFace = new Dictionary<long, List<(long PersonId, float Score)>>();
        foreach (var face in toScore)
        {
            float[] faceEmbedding = CcipEmbeddingService.BytesToEmbedding(face.Embedding!);
            // One CCIP metric-model call per face, scored against every eligible person's
            // individual references at once - see ComputeMatchScores' doc comment for why this
            // must be batched rather than one call per reference.
            float[] refScores = await Task.Run(() => ccipEmbedder.ComputeMatchScores(faceEmbedding, flatRefs));

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

        return new SuggestFacesResult(embedded, suggested, personRefs.Count, eliminationsApplied);
    }
}

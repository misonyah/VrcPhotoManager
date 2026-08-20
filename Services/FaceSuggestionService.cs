using VrcPhotoManager.Data;

namespace VrcPhotoManager.Services;

/// <summary>Result of a Suggest Faces run - see FaceSuggestionService.RunAsync's doc comment.
/// NoEligiblePeople is true when no registered person has enough reference material yet
/// (FaceMatcher.MinReferenceEmbeddings); callers should show that as its own message rather
/// than a "0 suggestions" result, since it means the run couldn't even attempt scoring.</summary>
public readonly record struct SuggestFacesResult(int Embedded, int Suggested, int EligiblePeople)
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
    /// Embeds every not-yet-embedded detected face, builds a reference centroid per registered
    /// person (confirmed-tag embeddings + VRC profile thumbnail), then scores every
    /// still-undetermined face against those centroids and records suggestions that clear
    /// FaceMatcher's acceptance bar. See FaceMatcher.cs for the scoring design (differential
    /// margin over the runner-up, not a raw similarity) and MainViewModel's former inline
    /// version (before this extraction) for the original call site.
    /// </summary>
    public static async Task<SuggestFacesResult> RunAsync(
        FaceRepository faces,
        ClipEmbeddingService clipEmbedder,
        IReadOnlyDictionary<long, string> pathByPhotoId,
        IReadOnlyDictionary<long, string?> avatarTypeByPhotoId,
        Action<string>? progress = null)
    {
        progress?.Invoke("Computing face embeddings...");
        var needingEmbedding = faces.GetDetectedFacesWithoutEmbedding();
        int embedded = 0;
        // See MainViewModel.ClassifyPhotosAsync for why bounded concurrency here is safe:
        // ClipEmbeddingService serializes its own session.Run() calls internally, so only the
        // CPU-bound preprocessing overlaps across threads.
        using var embedSemaphore = new SemaphoreSlim(Environment.ProcessorCount);
        var embedTasks = needingEmbedding.Select(async face =>
        {
            if (!pathByPhotoId.TryGetValue(face.PhotoId, out string? path)) return;
            await embedSemaphore.WaitAsync();
            try
            {
                float[] embedding = await Task.Run(() =>
                    clipEmbedder.ComputeEmbedding(path, face.X, face.Y, face.Width, face.Height));
                faces.SetEmbedding(face.Id, ClipEmbeddingService.EmbeddingToBytes(embedding));
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

        progress?.Invoke("Building reference centroids...");
        var persons = faces.GetAllPersons();
        var centroids = new Dictionary<long, float[]>();
        var confirmedPhotoIdsByPerson = new Dictionary<long, HashSet<long>>();
        foreach (var person in persons)
        {
            var refs = faces.GetReferenceEmbeddingsForPerson(person.Id)
                .Select(ClipEmbeddingService.BytesToEmbedding).ToList();
            if (person.VrcProfileThumbnail is byte[] thumb)
            {
                try { refs.Add(await Task.Run(() => clipEmbedder.ComputeEmbeddingFromBytes(thumb))); }
                catch { /* corrupt/unreadable thumbnail - skip it, may still have enough tag-derived refs */ }
            }

            var centroid = FaceMatcher.TryComputeCentroid(refs);
            if (centroid is not null)
            {
                centroids[person.Id] = centroid;
                confirmedPhotoIdsByPerson[person.Id] = faces.GetTaggedPhotoIdsForPerson(person.Id);
            }
        }

        if (centroids.Count == 0)
        {
            return new SuggestFacesResult(embedded, 0, 0);
        }

        progress?.Invoke("Matching faces against registered people...");
        var toScore = faces.GetFacesNeedingSuggestion();
        int suggested = 0;
        foreach (var face in toScore)
        {
            if (face.Embedding is null) continue;
            float[] faceEmbedding = ClipEmbeddingService.BytesToEmbedding(face.Embedding);

            var scored = centroids
                .Select(kv => (PersonId: kv.Key, Similarity: FaceMatcher.CosineSimilarity(faceEmbedding, kv.Value)))
                .OrderByDescending(s => s.Similarity)
                .ToList();

            var best = scored[0];
            bool accept;
            float confidence;
            if (scored.Count == 1)
            {
                accept = best.Similarity >= FaceMatcher.SingleCandidateThreshold;
                confidence = best.Similarity;
            }
            else
            {
                float margin = best.Similarity - scored[1].Similarity;
                accept = margin >= FaceMatcher.DifferentialMarginThreshold;
                confidence = margin;
            }

            if (!accept) continue;

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
            // a single-candidate raw similarity is a different scale entirely and would always exceed it,
            // so single-candidate suggestions are capped at ConfirmPrompt regardless of score.
            Models.SuggestionTier tier = scored.Count > 1 ? FaceMatcher.DetermineTier(combinedScore) : Models.SuggestionTier.ConfirmPrompt;
            Models.FaceLabelSource source = tier == Models.SuggestionTier.AutoTagged ? Models.FaceLabelSource.AutoTagged : Models.FaceLabelSource.EmbeddingMatch;

            faces.UpsertFaceLabel(face.Id, best.PersonId, confirmed: false, source, combinedScore);
            faces.UpsertSuggestionLog(face.Id, best.PersonId, combinedScore, confidence, avatarAffinityBoost, coOccurrenceBoost, tier);
            suggested++;
        }

        return new SuggestFacesResult(embedded, suggested, centroids.Count);
    }
}

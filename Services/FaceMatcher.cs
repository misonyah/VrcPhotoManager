using VrcPhotoManager.Models;

namespace VrcPhotoManager.Services;

/// <summary>
/// Pure scoring logic for face matching - no data access, so it's testable/reusable
/// independent of FaceRepository. See docs/superpowers/specs/2026-07-29-face-matching-design.md
/// for why differential scoring (margin over the next-best candidate) matters: a positive-only
/// reference misattributes photos between people who are frequently photographed together and
/// share a similar aesthetic - the normal case for friend groups.
///
/// All score-space constants below are in CcipEmbeddingService.ComputeMatchScores' units
/// (score = -distance from CCIP's own learned metric model, so higher is still better - see
/// its doc comment). This app originally used plain CLIP cosine similarity here instead;
/// switched after a real, measured comparison found CLIP - a general-purpose vision-language
/// model, never trained to discriminate between people/characters at all - gave one person
/// (MisoNyah, 62 reference photos, more than anyone else registered) a ZERO percent suggestion
/// rate: her best-candidate margin topped out at 0.094 against a 0.15 acceptance floor, no
/// matter how many of her reference photos were embedded. CCIP (deepghs/ccip_onnx, contrastively
/// trained specifically to tell anime characters apart) separated the same two people with only
/// 2.7% cross-confusion, and a leave-one-out validation (holding out each reference photo,
/// matching it against a centroid of the rest) correctly matched her 91.9% of the time versus
/// CLIP's 0%.
/// </summary>
public static class FaceMatcher
{
    /// <summary>A single reference photo picks up generic "VRChat selfie" style rather than
    /// the specific person (found during prior Python prototyping) - require several before
    /// trusting a centroid enough to suggest matches against it.</summary>
    public const int MinReferenceEmbeddings = 3;

    /// <summary>
    /// Estimated 2026-08-21 from a leave-one-out validation against two real people's reference
    /// photos (see the class doc comment): centroid-based within-person distance averaged 0.070-
    /// 0.107, and a single measured cross-person pair (MisoNyah vs. Tomaae) averaged 0.308 - a
    /// ~0.20 gap. Set to roughly half that gap, same "err toward fewer false positives" spirit as
    /// the original CLIP calibration this replaced, but this is a first-pass estimate from a
    /// two-person sample, not the larger multi-pool calibration run CLIP's original threshold
    /// had - expect to revise once SuggestionLog has enough real outcomes across more people to
    /// query.
    /// </summary>
    public const float DifferentialMarginThreshold = 0.10f;

    /// <summary>Used only when exactly one registered person is eligible (nothing to
    /// differentiate against). Reuses deepghs/ccip_onnx's own published same-character
    /// threshold (metrics.json: distance <= 0.178475, the F1-optimal cutoff over CCIP's own much
    /// larger calibration set) rather than guessing - a lone candidate needs to clear the
    /// model's own bar for "this is plausibly the same character" at all, not just "closest of a
    /// bad lot".</summary>
    public const float SingleCandidateThreshold = -0.178475f;

    /// <summary>Deliberately conservative placeholder values, favoring the existing
    /// ConfirmPrompt tier over AutoTagged until real SuggestionLog data justifies raising
    /// confidence in the combined score - see
    /// docs/superpowers/specs/2026-08-06-avatar-face-combined-matching-design.md. Scaled down
    /// from CLIP's original 0.05/0.05 in proportion to DifferentialMarginThreshold's own drop
    /// (0.15 -> 0.10); still not derived from any calibration run, still expected to be revised
    /// once SuggestionLog has real outcomes to query.</summary>
    public const float AvatarAffinityBoost = 0.03f;
    public const float CoOccurrenceBoost = 0.03f;

    /// <summary>Minimum number of photos two people must already be confirmed together in
    /// before a co-occurrence boost applies - avoids boosting off a single shared photo, same
    /// spirit as MinReferenceEmbeddings requiring several references before trusting a
    /// centroid.</summary>
    public const int MinCoOccurrenceCount = 3;

    /// <summary>Combined score (face match score + boosts) at or above this auto-tags without a
    /// confirm click, instead of the existing ConfirmPrompt behavior. Keeps the same ~2x ratio
    /// over DifferentialMarginThreshold (0.10) that CLIP's original 0.30-over-0.15 pairing used,
    /// so a match that only just cleared the base bar still can't reach AutoTagged on boosts
    /// alone - it still needs to be a genuinely strong match first.</summary>
    public const float AutoTagThreshold = 0.20f;

    /// <summary>Only ever called for a face that already passed the existing
    /// SingleCandidateThreshold/DifferentialMarginThreshold accept check - always returns a real
    /// tier, never "no suggestion" (that decision already happened before this is called).</summary>
    public static SuggestionTier DetermineTier(float combinedScore) =>
        combinedScore >= AutoTagThreshold ? SuggestionTier.AutoTagged : SuggestionTier.ConfirmPrompt;

    /// <summary>Reference sets at or above this size get outlier-trimmed before centroiding
    /// (see TrimOutliers) - below it, there's too little data to tell "noise" from "real
    /// signal" (dropping even one of a 3-4-photo set is as likely to hurt as help), so small
    /// sets are trusted as-is, same as before this trimming existed.</summary>
    public const int MinReferencesForTrimming = 6;

    /// <summary>How far below the mean similarity-to-centroid a reference embedding has to
    /// fall (in standard deviations) to count as an outlier worth dropping. Motivated by a real,
    /// measured case (originally under CLIP, before the CCIP switch, but the underlying cause is
    /// embedding-model-agnostic): MisoNyah's 62 reference crops span 44 different VRChat worlds
    /// versus Tomaae's 22 across just 10 (9 of them the same world) - neither CLIP nor CCIP's
    /// preprocessing does any lighting/color correction (two dedicated attempts at adding one,
    /// CLAHE and gray-world white balance, were both tried and measured to make no real
    /// difference - see git history), so a handful of oddly-lit crops can measurably smear an
    /// otherwise-tight centroid. 1.5 standard deviations is the conventional mild-outlier cutoff
    /// - aggressive enough to catch the worst offenders, conservative enough not to eat into a
    /// legitimately spread-out but real cluster.</summary>
    public const float OutlierStdDevThreshold = 1.5f;

    /// <summary>Never trims more than this fraction of a reference set, however far its worst
    /// members fall below the mean - a safety cap so one unlucky calibration run can't gut a
    /// person's entire reference pool.</summary>
    public const float MaxTrimFraction = 0.3f;

    /// <summary>Null if there aren't enough references yet - caller should skip this person
    /// for suggestions this pass, not suggest off an unreliable centroid.</summary>
    public static float[]? TryComputeCentroid(List<float[]> referenceEmbeddings)
    {
        if (referenceEmbeddings.Count < MinReferenceEmbeddings) return null;

        var kept = referenceEmbeddings.Count >= MinReferencesForTrimming
            ? TrimOutliers(referenceEmbeddings)
            : referenceEmbeddings;

        return Normalize(Sum(kept));
    }

    /// <summary>Drops reference embeddings that sit unusually far from the rest of the set -
    /// see OutlierStdDevThreshold/MaxTrimFraction. Two passes: a rough centroid built from
    /// EVERY reference measures each embedding's own similarity to "the group"; only the
    /// surviving subset feeds the real centroid TryComputeCentroid actually hands out.</summary>
    private static List<float[]> TrimOutliers(List<float[]> referenceEmbeddings)
    {
        float[]? roughCentroid = Normalize(Sum(referenceEmbeddings));
        if (roughCentroid is null) return referenceEmbeddings;

        var ranked = referenceEmbeddings
            .Select(e => (Embedding: e, Similarity: CosineSimilarity(e, roughCentroid)))
            .OrderBy(x => x.Similarity)
            .ToList();

        float mean = ranked.Average(x => x.Similarity);
        float variance = ranked.Sum(x => (x.Similarity - mean) * (x.Similarity - mean)) / ranked.Count;
        float cutoff = mean - OutlierStdDevThreshold * MathF.Sqrt(variance);

        int minKeep = Math.Max(MinReferenceEmbeddings,
            referenceEmbeddings.Count - (int)(referenceEmbeddings.Count * MaxTrimFraction));

        var kept = ranked.Where(x => x.Similarity >= cutoff).Select(x => x.Embedding).ToList();
        // ranked is ascending by similarity, so the last minKeep entries are the best-fitting -
        // used verbatim if the cutoff-based trim would have dropped more than MaxTrimFraction
        // allows.
        return kept.Count >= minKeep ? kept : ranked.TakeLast(minKeep).Select(x => x.Embedding).ToList();
    }

    private static float[] Sum(List<float[]> embeddings)
    {
        int dim = embeddings[0].Length;
        var sum = new float[dim];
        foreach (var embedding in embeddings)
        {
            for (int i = 0; i < dim; i++) sum[i] += embedding[i];
        }
        return sum;
    }

    /// <summary>Null if the input is a zero vector (e.g. embeddings that exactly cancel out) -
    /// vanishingly unlikely for real data, but a 0/0 division would otherwise silently produce
    /// a NaN-filled "centroid" that compares as similar to nothing.</summary>
    private static float[]? Normalize(float[] vector)
    {
        float norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm == 0) return null;
        var result = new float[vector.Length];
        for (int i = 0; i < vector.Length; i++) result[i] = vector[i] / norm;
        return result;
    }

    /// <summary>Cheap, model-free similarity used only internally by TrimOutliers to detect
    /// statistical outliers within one person's own reference set - NOT the real match score
    /// used to accept/reject a suggestion (see CcipEmbeddingService.ComputeMatchScores for that;
    /// CCIP's actual notion of "how similar are these two images" is a separate learned model,
    /// not a plain cosine formula). Plain cosine similarity in the raw embedding space is a
    /// perfectly good proxy for "does this reference look like the rest of this person's own
    /// set", and avoids an ONNX call for every reference photo during centroid-building. Both
    /// inputs are expected to already be L2-normalized (CcipEmbeddingService normalizes on
    /// output, and TryComputeCentroid normalizes its result) - so cosine similarity reduces to a
    /// plain dot product.</summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }
}

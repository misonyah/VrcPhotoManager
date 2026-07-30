namespace VrcPhotoManager.Services;

/// <summary>
/// Pure scoring logic for Phase 3 face matching - no data access, so it's testable/reusable
/// independent of FaceRepository. See docs/superpowers/specs/2026-07-29-face-matching-design.md
/// for why differential scoring (margin over the next-best candidate) matters: a positive-only
/// reference misattributes photos between people who are frequently photographed together and
/// share a similar aesthetic - the normal case for friend groups.
/// </summary>
public static class FaceMatcher
{
    /// <summary>A single reference photo picks up generic "VRChat selfie" style rather than
    /// the specific person (found during prior Python prototyping) - require several before
    /// trusting a centroid enough to suggest matches against it.</summary>
    public const int MinReferenceEmbeddings = 3;

    /// <summary>
    /// Calibrated 2026-07-30 against real face crops (via a temporary batch-crop diagnostic,
    /// since the reference photos are full screenshots, not pre-cropped): REDACTED's own
    /// "v4"-shortlist crops (50/50 detected) gave within-person similarity min=0.6478
    /// avg=0.8238 max=1.0000 (n=1225); comparing those against a MisoNyah-labeled candidate
    /// pool gave cross-person similarity min=0.2574 avg=0.5955 max=0.9623 (n=1100). MisoNyah's
    /// own within-group stats were discarded - the only large pool found for that person was an
    /// early, unrefined "v2" shortlist (vs. REDACTED's more mature "v4"), and it showed poor
    /// separation from the cross group, most likely because it still has real false-positive
    /// candidates mixed in. That doesn't invalidate the cross-group comparison itself, though -
    /// it only needs to be "very likely not REDACTED", which holds regardless of exactly how pure
    /// the other pool is. Set to roughly half the (within-avg - cross-avg) gap, erring toward
    /// fewer false positives per the "review, don't auto-confirm" design.
    /// </summary>
    public const float DifferentialMarginThreshold = 0.15f;

    /// <summary>Used only when exactly one registered person is eligible (nothing to
    /// differentiate against) - set comfortably above the cross-group average (0.5955) from the
    /// same calibration run, since a lone candidate needs to look convincingly like a match, not
    /// just "closest of a bad lot".</summary>
    public const float SingleCandidateThreshold = 0.70f;

    /// <summary>Null if there aren't enough references yet - caller should skip this person
    /// for suggestions this pass, not suggest off an unreliable centroid.</summary>
    public static float[]? TryComputeCentroid(List<float[]> referenceEmbeddings)
    {
        if (referenceEmbeddings.Count < MinReferenceEmbeddings) return null;

        int dim = referenceEmbeddings[0].Length;
        var sum = new float[dim];
        foreach (var embedding in referenceEmbeddings)
        {
            for (int i = 0; i < dim; i++) sum[i] += embedding[i];
        }

        float norm = MathF.Sqrt(sum.Sum(v => v * v));
        if (norm == 0) return null;
        for (int i = 0; i < dim; i++) sum[i] /= norm;
        return sum;
    }

    /// <summary>Both inputs are expected to already be L2-normalized (ClipEmbeddingService
    /// normalizes on output, and TryComputeCentroid normalizes its result) - so cosine
    /// similarity reduces to a plain dot product.</summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }
}

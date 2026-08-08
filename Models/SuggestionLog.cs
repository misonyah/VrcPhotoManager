namespace VrcPhotoManager.Models;

public enum SuggestionTier
{
    ConfirmPrompt,
    AutoTagged,
}

public enum SuggestionOutcome
{
    Pending,
    ConfirmedAsIs,
    CorrectedToDifferentPerson,
    Ignored,
}

/// <summary>One row per suggestion Suggest Faces actually writes (ConfirmPrompt or AutoTagged
/// tier only - a face that didn't clear the base face-similarity bar has nothing logged).
/// Outcome starts Pending and is resolved the first time a human reviews that face in
/// TagFacesWindow - see docs/superpowers/specs/2026-08-06-avatar-face-combined-matching-design.md
/// for what this is for (measuring real accuracy to recalibrate the boost weights and
/// auto-tag threshold from evidence instead of guessing).</summary>
public class SuggestionLog
{
    public long Id { get; set; }
    public long DetectedFaceId { get; set; }
    public long SuggestedPersonId { get; set; }
    public float CombinedScore { get; set; }
    public float FaceSimilarityScore { get; set; }
    public float AvatarAffinityBoost { get; set; }
    public float CoOccurrenceBoost { get; set; }
    public SuggestionTier Tier { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public SuggestionOutcome Outcome { get; set; } = SuggestionOutcome.Pending;
    public DateTime? OutcomeAt { get; set; }
}

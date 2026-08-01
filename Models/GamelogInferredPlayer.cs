namespace VrcPhotoManager.Models;

/// <summary>
/// One row per player inferred to have been present when a photo was taken, via
/// GamelogCorrelationService cross-referencing VRCX's own gamelog rather than the photo's own
/// (nonexistent) embedded metadata - a fallback for photos with no PhotoPlayer rows at all
/// (e.g. taken by someone else in the same instance). Deliberately a separate table from
/// PhotoPlayer, not blended into it: keeps genuinely VRCX-embedded data authoritative and lets
/// the UI tell the two sources apart ("per VRCX" vs "per log"). See
/// docs/superpowers/specs/2026-08-01-gamelog-player-inference-design.md.
/// </summary>
public class GamelogInferredPlayer
{
    public long Id { get; set; }
    public long PhotoId { get; set; }
    public required string UserId { get; set; }
    public required string DisplayName { get; set; }
}

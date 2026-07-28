namespace VrcdnManager.Models;

/// <summary>Manual = hand-picked by the user when registering a person. ExifElimination =
/// auto-bootstrapped by Phase 2's elimination labeling (see the design spec).</summary>
public enum ReferenceSource
{
    Manual,
    ExifElimination,
}

public class PersonReferencePhoto
{
    public long Id { get; set; }
    public long PersonId { get; set; }
    public long PhotoId { get; set; }
    public ReferenceSource Source { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

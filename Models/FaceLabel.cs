namespace VrcPhotoManager.Models;

public enum FaceLabelSource
{
    ExifElimination,
    EmbeddingMatch,
    Manual,
}

public class FaceLabel
{
    public long Id { get; set; }
    public long DetectedFaceId { get; set; }

    /// <summary>Null = deliberately marked "not a face" / unknown, distinct from having no
    /// FaceLabel row at all (= never reviewed).</summary>
    public long? PersonId { get; set; }

    public float Confidence { get; set; }
    public FaceLabelSource Source { get; set; }
    public bool Confirmed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

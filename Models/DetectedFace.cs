namespace VrcPhotoManager.Models;

public class DetectedFace
{
    public long Id { get; set; }
    public long PhotoId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>CLIP embedding as raw little-endian float32 bytes - null until Phase 3 computes
    /// it. Phase 1 only ever populates the bounding box.</summary>
    public byte[]? Embedding { get; set; }

    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}

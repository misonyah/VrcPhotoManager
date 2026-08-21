namespace VrcPhotoManager.Models;

public class DetectedFace
{
    public long Id { get; set; }
    public long PhotoId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>CCIP (deepghs/ccip_onnx) feature vector as raw little-endian float32 bytes -
    /// null until Suggest Faces computes it (see CcipEmbeddingService). Not comparable across a
    /// model swap - previously stored CLIP-based embeddings were wiped when this app switched
    /// from general-purpose CLIP to CCIP (trained specifically to discriminate anime
    /// characters), so a stale embedding computed under a since-replaced model is never a risk
    /// in practice, but would silently produce garbage distances if it ever were mixed in.</summary>
    public byte[]? Embedding { get; set; }

    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag - "Delete box" sets this instead of removing the row, so a
    /// dismissed false-positive detection (box size, position, embedding) stays around as
    /// labeled data for later reviewing/tuning detector quality, instead of being lost. Every
    /// read query in FaceRepository excludes Deleted rows; only diagnostics should ever query
    /// past this flag.</summary>
    public bool Deleted { get; set; }
}

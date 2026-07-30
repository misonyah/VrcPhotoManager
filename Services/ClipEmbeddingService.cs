using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace VrcPhotoManager.Services;

/// <summary>
/// Runs CLIP ViT-L-14 (laion2b_s32b_b82k)'s image encoder in-process via ONNX Runtime, to
/// score a detected face crop against a registered person's reference embeddings. Mirrors
/// WdTaggerService exactly (same DirectML/TryCreate pattern). Only the vision encoder is
/// needed - not the text encoder, since this only ever compares image to image.
///
/// Preprocessing (resize shortest side to 224 maintaining aspect ratio, center-crop to
/// 224x224, RGB, per-channel normalize) matches this specific model's own
/// visual/preprocess_cfg.json, fetched and checked during implementation rather than assumed
/// blind - this laion2b checkpoint uses mean=[0.5,0.5,0.5]/std=[0.5,0.5,0.5] (normalizing to
/// [-1,1]), NOT the original OpenAI CLIP ImageNet-derived mean/std some other CLIP variants
/// use - an assumption that would have been silently wrong without checking.
///
/// Per prior Python/GPU prototyping (see docs/superpowers/specs/2026-07-29-face-matching-design.md):
/// crop to the detected face BEFORE embedding - whole-image embeddings get dominated by scene
/// content (background, lighting, NSFW/skin-heavy frames) when the face is a small fraction of
/// the frame, causing false positives unrelated to who's actually pictured.
/// </summary>
public class ClipEmbeddingService
{
    private const int InputSize = 224;
    private static readonly float[] Mean = [0.5f, 0.5f, 0.5f];
    private static readonly float[] Std = [0.5f, 0.5f, 0.5f];

    private readonly InferenceSession _session;

    private ClipEmbeddingService(InferenceSession session)
    {
        _session = session;
    }

    public static ClipEmbeddingService? TryCreate(string modelDir, out string? error)
    {
        error = null;
        string modelPath = Path.Combine(modelDir, "model.onnx");
        if (!File.Exists(modelPath))
        {
            error = $"CLIP model not found at {modelDir} (expected model.onnx).";
            return null;
        }

        try
        {
            var sessionOptions = new SessionOptions();
            try { sessionOptions.AppendExecutionProvider_DML(); }
            catch { /* fall back to CPU EP silently if DirectML isn't available on this machine */ }

            var session = new InferenceSession(modelPath, sessionOptions);
            return new ClipEmbeddingService(session);
        }
        catch (Exception ex)
        {
            error = $"Failed to load CLIP model: {ex.Message}";
            return null;
        }
    }

    /// <summary>Crops the given bounding box out of the full photo (cloning it - a raw submat
    /// shares its parent's buffer and would keep the whole full-resolution photo alive in
    /// memory) and embeds it.</summary>
    public float[] ComputeEmbedding(string photoPath, int x, int y, int width, int height)
    {
        using var fullImage = Cv2.ImRead(photoPath, ImreadModes.Color);
        if (fullImage.Empty())
        {
            throw new InvalidDataException($"Could not read image: {photoPath}");
        }

        using var faceCrop = new Mat(fullImage, new Rect(x, y, width, height)).Clone();
        return ComputeEmbeddingFromMat(faceCrop);
    }

    /// <summary>Embeds an already-in-memory image (e.g. a RegisteredPerson's
    /// VrcProfileThumbnail blob) with no cropping - used for reference images that are
    /// already tightly-framed rather than a full screenshot needing a face bounding box.</summary>
    public float[] ComputeEmbeddingFromBytes(byte[] imageBytes)
    {
        using var mat = Cv2.ImDecode(imageBytes, ImreadModes.Color);
        if (mat.Empty())
        {
            throw new InvalidDataException("Could not decode image bytes.");
        }
        return ComputeEmbeddingFromMat(mat);
    }

    private float[] ComputeEmbeddingFromMat(Mat mat)
    {
        float[] input = Preprocess(mat);
        var tensor = new DenseTensor<float>(input, [1, 3, InputSize, InputSize]);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_session.InputMetadata.Keys.First(), tensor)]);
        float[] embedding = results.First().AsEnumerable<float>().ToArray();

        // L2-normalize so cosine similarity reduces to a plain dot product downstream.
        float norm = MathF.Sqrt(embedding.Sum(v => v * v));
        for (int i = 0; i < embedding.Length; i++) embedding[i] /= norm;
        return embedding;
    }

    private static float[] Preprocess(Mat source)
    {
        int srcW = source.Width, srcH = source.Height;
        double scale = (double)InputSize / Math.Min(srcW, srcH);
        int resizedW = (int)Math.Round(srcW * scale);
        int resizedH = (int)Math.Round(srcH * scale);

        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(resizedW, resizedH), interpolation: InterpolationFlags.Cubic);

        int cropX = Math.Max(0, (resizedW - InputSize) / 2);
        int cropY = Math.Max(0, (resizedH - InputSize) / 2);
        using var cropped = new Mat(resized, new Rect(cropX, cropY, InputSize, InputSize));

        using var rgb = new Mat();
        Cv2.CvtColor(cropped, rgb, ColorConversionCodes.BGR2RGB);

        // NCHW (channel-major) - unlike WdTaggerService's NHWC, this is CLIP's expected layout.
        float[] tensorData = new float[3 * InputSize * InputSize];
        for (int y = 0; y < InputSize; y++)
        {
            for (int x = 0; x < InputSize; x++)
            {
                Vec3b px = rgb.At<Vec3b>(y, x);
                for (int c = 0; c < 3; c++)
                {
                    float value = px[c] / 255f;
                    tensorData[c * InputSize * InputSize + y * InputSize + x] = (value - Mean[c]) / Std[c];
                }
            }
        }
        return tensorData;
    }

    public static byte[] EmbeddingToBytes(float[] embedding)
    {
        var bytes = new byte[embedding.Length * 4];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] BytesToEmbedding(byte[] bytes)
    {
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    public void Dispose() => _session.Dispose();
}

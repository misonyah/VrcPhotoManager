using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace VrcPhotoManager.Services;

/// <summary>
/// Runs deepghs/ccip_onnx ("Contrastive Anime Character Image Pre-Training", caformer-24-
/// randaug-pruned variant) in-process via ONNX Runtime, to score a detected face crop against
/// a registered person's reference embeddings. Replaced ClipEmbeddingService (general-purpose
/// CLIP, never trained to discriminate between people/characters at all - see FaceMatcher.cs's
/// doc comment) after a real, measured comparison: CCIP separated two real people's reference
/// photos with only 2.7% cross-person confusion, versus CLIP's suggestions topping out at a
/// 0.094 best-candidate margin for one person (MisoNyah, 62 reference photos) against a 0.15
/// acceptance floor - i.e. CLIP never once suggested her at all. A leave-one-out validation
/// (holding out each of a person's reference photos and matching it against the rest) found
/// 91.9% correctly matched under a single averaged centroid versus 100% under
/// nearest-neighbor scoring (best match against each individual reference) - see
/// FaceMatcher.GetTrimmedReferences, which is what FaceSuggestionService actually uses.
///
/// Unlike CLIP's plain cosine similarity, CCIP's actual "how similar are these two images"
/// comparison is a SEPARATE learned model (model_metrics.onnx, loaded here alongside the
/// feature extractor model_feat.onnx) - not a formula this app can special-case away. See
/// ComputeMatchScores.
///
/// Preprocessing (direct resize to 384x384 - no aspect-preserving crop, RGB, OpenAI CLIP's
/// standard ImageNet-derived mean/std) matches imgutils.metrics.ccip's own
/// _preprocess_image/_normalize functions (github.com/deepghs/imgutils), fetched and checked
/// during implementation rather than assumed blind.
/// </summary>
public class CcipEmbeddingService
{
    private const int InputSize = 384;
    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    private readonly InferenceSession _featSession;
    private readonly InferenceSession _metricsSession;
    // Two separate locks (not one shared) - each only guards concurrent Run() calls on ITS OWN
    // session (see WdTaggerService for the underlying DirectML gotcha: concurrent Run() calls
    // on the SAME session caused a real native crash); the two sessions are independent models,
    // so a feat-extraction call and a metrics call can safely run concurrently on different
    // threads.
    private readonly object _featLock = new();
    private readonly object _metricsLock = new();

    private CcipEmbeddingService(InferenceSession featSession, InferenceSession metricsSession)
    {
        _featSession = featSession;
        _metricsSession = metricsSession;
    }

    public static CcipEmbeddingService? TryCreate(string modelDir, out string? error)
    {
        error = null;
        string featPath = Path.Combine(modelDir, "model_feat.onnx");
        string metricsPath = Path.Combine(modelDir, "model_metrics.onnx");
        if (!File.Exists(featPath) || !File.Exists(metricsPath))
        {
            error = $"CCIP model not found at {modelDir} (expected model_feat.onnx and model_metrics.onnx).";
            return null;
        }

        try
        {
            InferenceSession CreateSession(string path)
            {
                var options = new SessionOptions();
                try { options.AppendExecutionProvider_DML(); }
                catch { /* fall back to CPU EP silently if DirectML isn't available on this machine */ }
                return new InferenceSession(path, options);
            }

            return new CcipEmbeddingService(CreateSession(featPath), CreateSession(metricsPath));
        }
        catch (Exception ex)
        {
            error = $"Failed to load CCIP model: {ex.Message}";
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
        float[] embedding;
        lock (_featLock)
        {
            var tensor = new DenseTensor<float>(input, [1, 3, InputSize, InputSize]);
            using var results = _featSession.Run([NamedOnnxValue.CreateFromTensor(_featSession.InputMetadata.Keys.First(), tensor)]);
            embedding = results.First().AsEnumerable<float>().ToArray();
        }

        // L2-normalize so FaceMatcher.TrimOutliers' own rough-centroid outlier-detection heuristic
        // (still used to filter a person's reference set even without a final centroid) behaves
        // consistently - CCIP's own metric model tolerates unnormalized input fine (confirmed
        // empirically), but every stored embedding in this app is expected to already be
        // unit-length (see FaceMatcher.CosineSimilarity's doc comment).
        float norm = MathF.Sqrt(embedding.Sum(v => v * v));
        for (int i = 0; i < embedding.Length; i++) embedding[i] /= norm;
        return embedding;
    }

    /// <summary>Runs CCIP's actual distance model against a batch of already-embedded feature
    /// vectors in ONE inference call, and returns a MATCH SCORE per candidate (query vs.
    /// candidates[i]) - higher is better, matching the rest of this app's existing "higher
    /// score = better match" convention (see FaceMatcher.cs) even though the underlying model
    /// outputs a DISTANCE (lower = more similar). model_metrics.onnx takes a [batch, 768] input
    /// and returns a [batch, batch] pairwise distance matrix over the WHOLE batch - not a
    /// per-pair call - so comparing one face against N reference centroids in N separate calls
    /// would be N times slower for no benefit; this concatenates [query, ...candidates] into a
    /// single batch and reads back just the first row (query-to-each-candidate), ignoring the
    /// rest of the matrix.</summary>
    public float[] ComputeMatchScores(float[] query, IReadOnlyList<float[]> candidates)
    {
        if (candidates.Count == 0) return [];

        int dim = query.Length;
        int batchSize = candidates.Count + 1;
        var batch = new float[batchSize * dim];
        Array.Copy(query, 0, batch, 0, dim);
        for (int i = 0; i < candidates.Count; i++)
        {
            Array.Copy(candidates[i], 0, batch, (i + 1) * dim, dim);
        }

        float[] distances;
        lock (_metricsLock)
        {
            var tensor = new DenseTensor<float>(batch, [batchSize, dim]);
            using var results = _metricsSession.Run([NamedOnnxValue.CreateFromTensor(_metricsSession.InputMetadata.Keys.First(), tensor)]);
            distances = results.First().AsEnumerable<float>().ToArray();
        }

        // distances is the flattened [batchSize, batchSize] matrix, row-major - row 0 (indices
        // 0..batchSize-1) is "query vs. everyone", and index 0 within that row is "query vs.
        // itself" (always ~0, skipped).
        var scores = new float[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            scores[i] = -distances[i + 1];
        }
        return scores;
    }

    private static float[] Preprocess(Mat source)
    {
        // Direct resize to InputSize x InputSize - deliberately NOT aspect-preserving (no
        // resize-then-center-crop like ClipEmbeddingService used) - imgutils' own
        // _preprocess_image does a plain `image.resize((size, size))`, so a non-square face
        // crop gets squashed the same way CCIP was actually trained/evaluated, and "fixing"
        // that to preserve aspect ratio would just be a different, untested preprocessing path.
        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(InputSize, InputSize), interpolation: InterpolationFlags.Linear);

        using var rgb = new Mat();
        Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

        // NCHW (channel-major), same layout convention as ClipEmbeddingService.
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

    public void Dispose()
    {
        _featSession.Dispose();
        _metricsSession.Dispose();
    }
}

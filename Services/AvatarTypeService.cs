using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace VrcPhotoManager.Services;

/// <summary>
/// Runs the avatar-base classifier trained by tools/avatar-scraper (see
/// docs/superpowers/plans/2026-08-02-avatar-booth-training-pipeline.md) via ONNX
/// Runtime, same shape as WdTaggerService. Preprocessing must match that pipeline's
/// train.py/export_onnx.py exactly: RGB (not WD14's BGR), resized+padded to a 224x224
/// white square, NCHW layout, ImageNet mean/std normalization (not WD14's raw
/// 0-255) - get any of these wrong and every prediction is garbage.
/// </summary>
public class AvatarTypeService : IDisposable
{
    private const int InputSize = 224;
    private const float AcceptanceThreshold = 0.5f;

    /// <summary>Plan A's (tools/avatar-scraper) deliberate negative/"none of the above" class -
    /// see its README "Negative/unknown class" section. A confident match against this class
    /// means "this is not a recognized avatar", not a real avatar name, so Classify() treats it
    /// the same as a below-threshold result.</summary>
    private const string NegativeClassLabel = "unknown-manual";

    private static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] ImageNetStd = [0.229f, 0.224f, 0.225f];

    private readonly InferenceSession _session;
    private readonly string[] _labels;
    // Parallel to _labels (same index order) - the stable "booth:<item id>"/"local:NNNN"
    // identity from the avatar-scraper's catalog_ids.py, unlike _labels' display text which
    // can freely change as boilerplate-stripping rules improve. Null when catalog_ids.txt
    // wasn't present in modelDir (older downloaded models, or a model built before this file
    // existed) - CatalogId is then just null for everything, degrading gracefully rather than
    // failing to load.
    private readonly string?[]? _catalogIds;
    // Guards session.Run() only - see WdTaggerService for why (concurrent Run() calls on a
    // DirectML session caused a real native crash; Preprocess() is unaffected and safe to
    // run concurrently across threads).
    private readonly object _inferenceLock = new();

    private AvatarTypeService(InferenceSession session, string[] labels, string?[]? catalogIds)
    {
        _session = session;
        _labels = labels;
        _catalogIds = catalogIds;
    }

    public static AvatarTypeService? TryCreate(string modelDir, out string? error)
    {
        error = null;
        string modelPath = Path.Combine(modelDir, "model.onnx");
        string labelsPath = Path.Combine(modelDir, "labels.txt");
        if (!File.Exists(modelPath) || !File.Exists(labelsPath))
        {
            error = $"Avatar model not found at {modelDir} (expected model.onnx + labels.txt).";
            return null;
        }

        try
        {
            var sessionOptions = new SessionOptions();
            try { sessionOptions.AppendExecutionProvider_DML(); }
            catch { /* fall back to CPU EP silently if DirectML isn't available */ }

            var session = new InferenceSession(modelPath, sessionOptions);

            // Only trim a trailing blank line (export_onnx.py's write_text can leave one) -
            // do NOT filter blank lines anywhere else in the list. Dropping an interior blank
            // line would silently shift every subsequent label's index relative to the
            // model's actual class indices, corrupting predictions with no error at all.
            string[] labels = File.ReadAllLines(labelsPath);
            if (labels.Length > 0 && labels[^1].Length == 0)
            {
                labels = labels[..^1];
            }

            int outputClasses = session.OutputMetadata.Values.First().Dimensions[1];
            if (labels.Length != outputClasses)
            {
                error = $"labels.txt has {labels.Length} labels but model.onnx outputs {outputClasses} classes - mismatched model/labels pair.";
                session.Dispose();
                return null;
            }

            // Optional - not every model directory has one (older downloads predate
            // catalog_ids.txt). A line-count mismatch is treated the same as "absent" rather
            // than a load failure, since it just means a stale/half-updated model folder;
            // CatalogId simply comes back null for everything until re-downloaded.
            string catalogIdsPath = Path.Combine(modelDir, "catalog_ids.txt");
            string?[]? catalogIds = null;
            if (File.Exists(catalogIdsPath))
            {
                string[] rawCatalogIds = File.ReadAllLines(catalogIdsPath);
                if (rawCatalogIds.Length > 0 && rawCatalogIds[^1].Length == 0)
                {
                    rawCatalogIds = rawCatalogIds[..^1];
                }
                if (rawCatalogIds.Length == labels.Length)
                {
                    catalogIds = rawCatalogIds!;
                }
            }

            return new AvatarTypeService(session, labels, catalogIds);
        }
        catch (Exception ex)
        {
            error = $"Failed to load avatar model: {ex.Message}";
            return null;
        }
    }

    /// <summary>Returns the top-scoring avatar type, or a null Label (with the real
    /// confidence still reported) when the top score doesn't clear AcceptanceThreshold -
    /// callers store this as "no confident match" rather than forcing a guess. CatalogId is
    /// the stable identity for whatever Label resolves to (null exactly when Label is null, or
    /// when this model directory has no catalog_ids.txt - see the _catalogIds field doc).</summary>
    public (string? Label, string? CatalogId, float Confidence) Classify(string imagePath) =>
        Classify(imagePath, cropRegion: null);

    /// <summary>Region-scoped overload - crops to (x, y, width, height) in the source image's
    /// native pixel space before the same preprocessing/inference pipeline, so an individual
    /// detected avatar body (AvatarBodyDetectionService) can be classified on its own instead of
    /// the whole photo. See docs/superpowers/specs/2026-08-02-avatar-type-detector-design.md's
    /// "Explicit v1 scope boundaries" for why this didn't exist originally (multi-avatar
    /// disambiguation needs a body-detection step first, which this pairs with).</summary>
    public (string? Label, string? CatalogId, float Confidence) Classify(string imagePath, (int X, int Y, int Width, int Height)? cropRegion)
    {
        float[] input = Preprocess(imagePath, cropRegion);
        float[] logits;
        lock (_inferenceLock)
        {
            var tensor = new DenseTensor<float>(input, [1, 3, InputSize, InputSize]);
            using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_session.InputMetadata.Keys.First(), tensor)]);
            logits = results.First().AsEnumerable<float>().ToArray();
        }
        float[] probabilities = Softmax(logits);

        int bestIndex = 0;
        for (int i = 1; i < probabilities.Length; i++)
        {
            if (probabilities[i] > probabilities[bestIndex]) bestIndex = i;
        }

        float confidence = probabilities[bestIndex];
        bool isConfidentRealClass = confidence >= AcceptanceThreshold && _labels[bestIndex] != NegativeClassLabel;
        string? label = isConfidentRealClass ? _labels[bestIndex] : null;
        string? catalogId = isConfidentRealClass ? _catalogIds?[bestIndex] : null;
        return (label, catalogId, confidence);
    }

    private static float[] Softmax(float[] logits)
    {
        float max = logits.Max();
        float[] exp = logits.Select(x => MathF.Exp(x - max)).ToArray();
        float sum = exp.Sum();
        return exp.Select(x => x / sum).ToArray();
    }

    /// <summary>WPF's BitmapDecoder is the primary path (matches the rest of this method's
    /// resize/pad math exactly), but WIC's metadata parser can reject a JPEG outright -
    /// COMException 0x88982F8E "Unexpected property type or value" - over a malformed/unusual
    /// EXIF property even when the actual pixel data is perfectly fine elsewhere (confirmed
    /// against a real photo: both Pillow and OpenCV decode it without complaint). Falling back
    /// to OpenCvSharp - already a project dependency, used by AvatarBodyDetectionService/
    /// FaceDetectionService, and doesn't parse metadata at all - recovers those photos instead
    /// of just failing them.</summary>
    private static float[] Preprocess(string imagePath, (int X, int Y, int Width, int Height)? cropRegion = null)
    {
        try
        {
            return PreprocessWpf(imagePath, cropRegion);
        }
        catch (FileFormatException)
        {
            return PreprocessOpenCv(imagePath, cropRegion);
        }
    }

    private static float[] PreprocessWpf(string imagePath, (int X, int Y, int Width, int Height)? cropRegion)
    {
        var decoder = BitmapDecoder.Create(new Uri(imagePath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];

        if (cropRegion is (int x, int y, int width, int height))
        {
            // Clamped against the actual decoded size - a detected body box can end up
            // fractionally outside the frame after NMS/rounding, and CroppedBitmap throws
            // rather than clamping itself.
            int clampedX = Math.Clamp(x, 0, source.PixelWidth - 1);
            int clampedY = Math.Clamp(y, 0, source.PixelHeight - 1);
            int clampedWidth = Math.Clamp(width, 1, source.PixelWidth - clampedX);
            int clampedHeight = Math.Clamp(height, 1, source.PixelHeight - clampedY);
            source = new CroppedBitmap(source, new Int32Rect(clampedX, clampedY, clampedWidth, clampedHeight));
        }

        if (source.PixelWidth > 1024 || source.PixelHeight > 1024)
        {
            double scale = 1024.0 / Math.Max(source.PixelWidth, source.PixelHeight);
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }

        double side = Math.Max(source.PixelWidth, source.PixelHeight);
        double drawScale = InputSize / side;
        double drawWidth = source.PixelWidth * drawScale;
        double drawHeight = source.PixelHeight * drawScale;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, InputSize, InputSize));
            dc.DrawImage(source, new System.Windows.Rect((InputSize - drawWidth) / 2, (InputSize - drawHeight) / 2, drawWidth, drawHeight));
        }
        var rtb = new RenderTargetBitmap(InputSize, InputSize, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        int stride = InputSize * 4;
        byte[] pixels = new byte[InputSize * InputSize * 4];
        rtb.CopyPixels(pixels, stride, 0);

        // NCHW, RGB order, ImageNet-normalized - deliberately different from WD14's
        // NHWC/BGR/raw-0-255 (see class doc comment). WPF's Pbgra32 byte layout is
        // B,G,R,A per pixel, so R and B are swapped relative to WD14's Preprocess.
        float[] tensorData = new float[3 * InputSize * InputSize];
        int channelSize = InputSize * InputSize;
        int pixelIndex = 0;
        for (int p = 0; p < pixels.Length; p += 4, pixelIndex++)
        {
            float r = pixels[p + 2] / 255f;
            float g = pixels[p + 1] / 255f;
            float b = pixels[p] / 255f;
            tensorData[pixelIndex] = (r - ImageNetMean[0]) / ImageNetStd[0];
            tensorData[channelSize + pixelIndex] = (g - ImageNetMean[1]) / ImageNetStd[1];
            tensorData[2 * channelSize + pixelIndex] = (b - ImageNetMean[2]) / ImageNetStd[2];
        }
        return tensorData;
    }

    /// <summary>Same resize (aspect-preserving, longer side to InputSize) + white-pad-to-square
    /// + ImageNet normalization as PreprocessWpf, just built on OpenCvSharp's Mat instead of
    /// WPF's BitmapSource - see Preprocess's doc comment for why this fallback exists. Not held
    /// to pixel-identical parity with PreprocessWpf (unlike the train.py/C# parity this class's
    /// own doc comment warns about) - this path only ever runs for a file WPF already couldn't
    /// decode at all, so "produces a working classification" matters here, not exact parity
    /// with a decode that never happens for these files anyway.</summary>
    private static float[] PreprocessOpenCv(string imagePath, (int X, int Y, int Width, int Height)? cropRegion)
    {
        using var decoded = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (decoded.Empty())
        {
            throw new InvalidDataException($"Could not read image: {imagePath}");
        }

        Mat source = decoded;
        if (cropRegion is (int x, int y, int width, int height))
        {
            int clampedX = Math.Clamp(x, 0, source.Width - 1);
            int clampedY = Math.Clamp(y, 0, source.Height - 1);
            int clampedWidth = Math.Clamp(width, 1, source.Width - clampedX);
            int clampedHeight = Math.Clamp(height, 1, source.Height - clampedY);
            source = new Mat(source, new OpenCvSharp.Rect(clampedX, clampedY, clampedWidth, clampedHeight));
        }

        if (source.Width > 1024 || source.Height > 1024)
        {
            double scale1024 = 1024.0 / Math.Max(source.Width, source.Height);
            var downscaled = new Mat();
            Cv2.Resize(source, downscaled, new OpenCvSharp.Size(0, 0), scale1024, scale1024, InterpolationFlags.Area);
            source = downscaled;
        }

        double side = Math.Max(source.Width, source.Height);
        double drawScale = InputSize / side;
        int drawWidth = Math.Max(1, (int)Math.Round(source.Width * drawScale));
        int drawHeight = Math.Max(1, (int)Math.Round(source.Height * drawScale));

        using var resized = new Mat();
        Cv2.Resize(source, resized, new OpenCvSharp.Size(drawWidth, drawHeight), interpolation: InterpolationFlags.Area);

        using var canvas = new Mat(InputSize, InputSize, MatType.CV_8UC3, new Scalar(255, 255, 255));
        int offsetX = (InputSize - drawWidth) / 2;
        int offsetY = (InputSize - drawHeight) / 2;
        resized.CopyTo(canvas[new OpenCvSharp.Rect(offsetX, offsetY, drawWidth, drawHeight)]);

        using var rgb = new Mat();
        Cv2.CvtColor(canvas, rgb, ColorConversionCodes.BGR2RGB);

        float[] tensorData = new float[3 * InputSize * InputSize];
        int channelSize = InputSize * InputSize;
        for (int py = 0; py < InputSize; py++)
        {
            for (int pxi = 0; pxi < InputSize; pxi++)
            {
                Vec3b pixel = rgb.At<Vec3b>(py, pxi);
                int pixelIndex = py * InputSize + pxi;
                tensorData[pixelIndex] = (pixel[0] / 255f - ImageNetMean[0]) / ImageNetStd[0];
                tensorData[channelSize + pixelIndex] = (pixel[1] / 255f - ImageNetMean[1]) / ImageNetStd[1];
                tensorData[2 * channelSize + pixelIndex] = (pixel[2] / 255f - ImageNetMean[2]) / ImageNetStd[2];
            }
        }
        return tensorData;
    }

    /// <summary>Every (Label, CatalogId) pair this model knows, regardless of whether Classify
    /// has ever actually matched a photo to it - powers Tag Faces' Avatar-mode search picker,
    /// which needs to let you tag a region as any known avatar, not just ones already surfaced
    /// by auto-classification.</summary>
    public IReadOnlyList<(string Label, string? CatalogId)> AllEntries =>
        _labels.Select((label, i) => (label, _catalogIds?[i])).ToList();

    public void Dispose() => _session.Dispose();
}

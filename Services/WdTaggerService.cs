using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VrcdnManager.Services;

/// <summary>
/// Runs the same WD14 tagger model used by the Python pipeline (D:\AI-Tools\wd14-tagger)
/// directly in-process via ONNX Runtime/DirectML, so this app doesn't depend on the
/// separate Python tool to classify newly-scanned photos. Reuses the already-downloaded
/// model files rather than re-fetching/duplicating them.
///
/// Preprocessing must match the Python version exactly (see the vrc-photo-triage skill):
/// RGBA composited onto a white background, padded to square, resized to 448x448, BGR
/// channel order, raw 0-255 float32 (not normalized). Get any of these wrong and the
/// output is garbage. Compositing+padding is done in one pass via WPF's DrawingVisual/
/// RenderTargetBitmap (drawing onto a white-filled square canvas), which handles alpha
/// blending correctly without manual per-pixel math.
/// </summary>
public class WdTaggerService : IDisposable
{
    private const int InputSize = 448;
    private static readonly string[] RatingNames = ["general", "sensitive", "questionable", "explicit"];

    private readonly InferenceSession _session;
    private readonly int[] _ratingIndices;

    private WdTaggerService(InferenceSession session, int[] ratingIndices)
    {
        _session = session;
        _ratingIndices = ratingIndices;
    }

    public static WdTaggerService? TryCreate(string modelDir, out string? error)
    {
        error = null;
        string modelPath = Path.Combine(modelDir, "model.onnx");
        string tagsPath = Path.Combine(modelDir, "selected_tags.csv");
        if (!File.Exists(modelPath) || !File.Exists(tagsPath))
        {
            error = $"WD14 model not found at {modelDir} (expected model.onnx + selected_tags.csv).";
            return null;
        }

        try
        {
            var sessionOptions = new SessionOptions();
            try { sessionOptions.AppendExecutionProvider_DML(); }
            catch { /* fall back to CPU EP silently if DirectML isn't available on this machine */ }

            var session = new InferenceSession(modelPath, sessionOptions);
            int[] ratingIndices = ParseRatingIndices(tagsPath);
            return new WdTaggerService(session, ratingIndices);
        }
        catch (Exception ex)
        {
            error = $"Failed to load WD14 model: {ex.Message}";
            return null;
        }
    }

    private static int[] ParseRatingIndices(string tagsCsvPath)
    {
        // CSV columns: tag_id,name,category,count - row order matches model output index order.
        var lines = File.ReadAllLines(tagsCsvPath);
        var indices = new int[RatingNames.Length];
        Array.Fill(indices, -1);

        for (int row = 1; row < lines.Length; row++) // skip header
        {
            var parts = lines[row].Split(',');
            if (parts.Length < 3) continue;
            string name = parts[1];
            string category = parts[2];
            if (category != "9") continue;

            int ratingSlot = Array.IndexOf(RatingNames, name);
            if (ratingSlot >= 0) indices[ratingSlot] = row - 1; // -1: header doesn't count toward output index
        }

        if (Array.IndexOf(indices, -1) >= 0)
            throw new InvalidOperationException("Could not find all 4 rating tags (general/sensitive/questionable/explicit) in selected_tags.csv.");

        return indices;
    }

    /// <summary>Returns the top-scoring rating (general/sensitive/questionable/explicit).</summary>
    public string ClassifyRating(string imagePath)
    {
        float[] input = Preprocess(imagePath);
        var tensor = new DenseTensor<float>(input, [1, InputSize, InputSize, 3]);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_session.InputMetadata.Keys.First(), tensor)]);
        var output = results.First().AsEnumerable<float>().ToArray();

        int bestSlot = 0;
        for (int i = 1; i < _ratingIndices.Length; i++)
        {
            if (output[_ratingIndices[i]] > output[_ratingIndices[bestSlot]]) bestSlot = i;
        }
        return RatingNames[bestSlot];
    }

    private static float[] Preprocess(string imagePath)
    {
        var decoder = BitmapDecoder.Create(new Uri(imagePath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];

        // Cap the initial decode - the model only sees 448x448 in the end, no need to hold a
        // multi-thousand-pixel original in memory just to immediately downscale it.
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
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, InputSize, InputSize));
            dc.DrawImage(source, new Rect((InputSize - drawWidth) / 2, (InputSize - drawHeight) / 2, drawWidth, drawHeight));
        }
        var rtb = new RenderTargetBitmap(InputSize, InputSize, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        int stride = InputSize * 4;
        byte[] pixels = new byte[InputSize * InputSize * 4];
        rtb.CopyPixels(pixels, stride, 0);

        // NHWC, BGR order (matches WPF's native Bgra32/Pbgra32 byte layout - B,G,R,A per
        // pixel), raw 0-255 range - do NOT normalize to 0-1, the model expects raw values.
        float[] tensorData = new float[InputSize * InputSize * 3];
        int t = 0;
        for (int p = 0; p < pixels.Length; p += 4)
        {
            tensorData[t++] = pixels[p];     // B
            tensorData[t++] = pixels[p + 1]; // G
            tensorData[t++] = pixels[p + 2]; // R
        }
        return tensorData;
    }

    public void Dispose() => _session.Dispose();
}

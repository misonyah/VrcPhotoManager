using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

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

    private AvatarTypeService(InferenceSession session, string[] labels)
    {
        _session = session;
        _labels = labels;
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

            return new AvatarTypeService(session, labels);
        }
        catch (Exception ex)
        {
            error = $"Failed to load avatar model: {ex.Message}";
            return null;
        }
    }

    /// <summary>Returns the top-scoring avatar type, or a null Label (with the real
    /// confidence still reported) when the top score doesn't clear AcceptanceThreshold -
    /// callers store this as "no confident match" rather than forcing a guess.</summary>
    public (string? Label, float Confidence) Classify(string imagePath)
    {
        float[] input = Preprocess(imagePath);
        var tensor = new DenseTensor<float>(input, [1, 3, InputSize, InputSize]);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_session.InputMetadata.Keys.First(), tensor)]);
        float[] logits = results.First().AsEnumerable<float>().ToArray();
        float[] probabilities = Softmax(logits);

        int bestIndex = 0;
        for (int i = 1; i < probabilities.Length; i++)
        {
            if (probabilities[i] > probabilities[bestIndex]) bestIndex = i;
        }

        float confidence = probabilities[bestIndex];
        bool isConfidentRealClass = confidence >= AcceptanceThreshold && _labels[bestIndex] != NegativeClassLabel;
        string? label = isConfidentRealClass ? _labels[bestIndex] : null;
        return (label, confidence);
    }

    private static float[] Softmax(float[] logits)
    {
        float max = logits.Max();
        float[] exp = logits.Select(x => MathF.Exp(x - max)).ToArray();
        float sum = exp.Sum();
        return exp.Select(x => x / sum).ToArray();
    }

    private static float[] Preprocess(string imagePath)
    {
        var decoder = BitmapDecoder.Create(new Uri(imagePath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];

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

    public void Dispose() => _session.Dispose();
}

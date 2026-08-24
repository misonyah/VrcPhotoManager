using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace VrcPhotoManager.Services;

public record BodyBox(int X, int Y, int Width, int Height);

/// <summary>
/// Anime/stylized full-body detector - deepghs/anime_person_detection's person_detect_v1.3_s
/// (YOLOv8s, ONNX, F1=0.86 per its own published threshold.json), same architecture family and
/// file/metadata conventions as FaceDetectionService's model, from the same source. Exists so
/// "Classify Avatars" can identify each individual avatar in a group photo instead of producing
/// one whole-frame guess (see docs/superpowers/specs/2026-08-02-avatar-type-detector-design.md's
/// "Explicit v1 scope boundaries" - that design deliberately deferred automatic multi-avatar
/// disambiguation as "a materially bigger project", assuming a body-detection dataset would need
/// to be built from scratch; this pretrained model, once found, made that assumption obsolete).
///
/// Validated directly against real photos before building this (same rigor as the face detector
/// swap): a 51-person group photo found all 51 bodies; a cluttered 4-avatar photo with
/// foliage/decorations in frame found all 4 real avatars with zero false positives on the
/// background scenery; a photo where 1 of 4 avatars was almost entirely cropped off-frame
/// correctly found only the 3 fully-visible ones rather than guessing at the missing one.
/// MaxInferSize=1280 (not FaceDetectionService's 3200) because that already recovered the full
/// 51/51 count on the group-photo stress case - no evidence a larger canvas is needed for this
/// model/threshold, and it's meaningfully cheaper to run.
///
/// Standard YOLOv8 output layout, same as FaceDetectionService: input 'images'
/// [batch,3,H,W], output 'output0' [batch, 4+numClasses, numAnchors], numClasses=1 (this model
/// only ever predicts "person").
/// </summary>
public class AvatarBodyDetectionService
{
    /// <summary>See the class doc comment for why this is lower than FaceDetectionService's
    /// 3200 - already validated to recover full recall on a 51-person group photo at this
    /// size.</summary>
    private const int MaxInferSize = 1280;

    /// <summary>YOLOv8's fully-convolutional backbone needs input dimensions that are multiples
    /// of its total downsampling stride - 32 is standard for this architecture, same as
    /// FaceDetectionService.</summary>
    private const int Align = 32;

    /// <summary>deepghs' own published F1-optimal threshold for person_detect_v1.3_s
    /// specifically (threshold.json: F1=0.86 at this exact value) - reused rather than
    /// guessing, same spirit as FaceDetectionService's ConfThreshold.</summary>
    private const float ConfThreshold = 0.324f;

    /// <summary>Standard YOLO NMS default - matches FaceDetectionService/deepghs' own
    /// yolo_predict wrapper.</summary>
    private const float IouThreshold = 0.7f;

    /// <summary>Padding color for the letterbox canvas - conventional YOLO mid-gray
    /// (114,114,114), matching FaceDetectionService and deepghs' own preprocessing.</summary>
    private static readonly Scalar PadColor = new(114, 114, 114);

    private readonly InferenceSession _session;
    // Guards session.Run() only - see FaceDetectionService/WdTaggerService for the underlying
    // DirectML gotcha (concurrent Run() calls on the same session caused a real native crash);
    // preprocessing is unaffected and safe to run concurrently across threads.
    private readonly object _inferenceLock = new();

    private AvatarBodyDetectionService(InferenceSession session)
    {
        _session = session;
    }

    public static AvatarBodyDetectionService? TryCreate(string modelDir, out string? error)
    {
        error = null;
        string modelPath = Path.Combine(modelDir, "model.onnx");
        if (!File.Exists(modelPath))
        {
            error = $"Avatar body detection model not found at {modelDir} (expected model.onnx).";
            return null;
        }

        try
        {
            var options = new SessionOptions();
            try { options.AppendExecutionProvider_DML(); }
            catch { /* fall back to CPU EP silently if DirectML isn't available on this machine */ }
            return new AvatarBodyDetectionService(new InferenceSession(modelPath, options));
        }
        catch (Exception ex)
        {
            error = $"Failed to load avatar body detection model: {ex.Message}";
            return null;
        }
    }

    /// <summary>Detects every avatar body in the image. Throws if the image can't even be read
    /// (missing file, locked/partially-written file, corrupt data, or the classic
    /// OpenCV-on-Windows trap of a non-ASCII path silently failing Cv2.ImRead) - same convention
    /// as FaceDetectionService.DetectFaces, so callers can distinguish that from a real "zero
    /// bodies found" result.</summary>
    public List<BodyBox> DetectBodies(string imagePath)
    {
        using var img = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (img.Empty())
        {
            throw new InvalidDataException($"Could not read image: {imagePath}");
        }

        var (tensorData, padW, padH, scaleToOriginal) = Preprocess(img);

        Tensor<float> outputTensor;
        lock (_inferenceLock)
        {
            var inputTensor = new DenseTensor<float>(tensorData, [1, 3, padH, padW]);
            using var results = _session.Run([NamedOnnxValue.CreateFromTensor("images", inputTensor)]);
            var raw = results.First(r => r.Name == "output0").AsTensor<float>();
            outputTensor = new DenseTensor<float>(raw.ToArray(), raw.Dimensions.ToArray());
        }

        return Postprocess(outputTensor, scaleToOriginal);
    }

    /// <summary>Aspect-preserving resize (longer side to at most MaxInferSize) then right/bottom
    /// pad to a multiple of Align - identical approach to FaceDetectionService.Preprocess.</summary>
    private static (float[] TensorData, int PadW, int PadH, double ScaleToOriginal) Preprocess(Mat bgr)
    {
        int oldW = bgr.Width, oldH = bgr.Height;
        double scale = Math.Min((double)MaxInferSize / oldW, (double)MaxInferSize / oldH);
        int newW = oldW, newH = oldH;
        if (scale < 1)
        {
            newW = (int)(oldW * scale);
            newH = (int)(oldH * scale);
        }
        int padW = ((newW + Align - 1) / Align) * Align;
        int padH = ((newH + Align - 1) / Align) * Align;

        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new Size(newW, newH), interpolation: InterpolationFlags.Linear);

        using var canvas = new Mat(padH, padW, MatType.CV_8UC3, PadColor);
        resized.CopyTo(canvas[new Rect(0, 0, newW, newH)]);

        using var rgb = new Mat();
        Cv2.CvtColor(canvas, rgb, ColorConversionCodes.BGR2RGB);

        float[] tensorData = new float[3 * padH * padW];
        for (int y = 0; y < padH; y++)
        {
            for (int x = 0; x < padW; x++)
            {
                Vec3b px = rgb.At<Vec3b>(y, x);
                for (int c = 0; c < 3; c++)
                {
                    tensorData[c * padH * padW + y * padW + x] = px[c] / 255f;
                }
            }
        }

        double scaleToOriginal = (double)oldW / newW;
        return (tensorData, padW, padH, scaleToOriginal);
    }

    /// <summary>output is [1, 4+numClasses, numAnchors] - row 0-3 are box center-x/center-y/
    /// width/height in padded-canvas pixel space, row 4 is the "person" confidence score (this
    /// model only ever predicts that one class). Confidence-filters, then NMS, then maps
    /// surviving boxes back to original-image pixel coordinates.</summary>
    private static List<BodyBox> Postprocess(Tensor<float> output, double scaleToOriginal)
    {
        int numAnchors = output.Dimensions[2];
        var candidates = new List<(float X0, float Y0, float X1, float Y1, float Score)>();
        for (int i = 0; i < numAnchors; i++)
        {
            float score = output[0, 4, i];
            if (score <= ConfThreshold) continue;

            float cx = output[0, 0, i];
            float cy = output[0, 1, i];
            float w = output[0, 2, i];
            float h = output[0, 3, i];
            candidates.Add((cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2, score));
        }

        var kept = Nms(candidates, IouThreshold);
        return kept.Select(b => new BodyBox(
            X: (int)Math.Round(b.X0 * scaleToOriginal),
            Y: (int)Math.Round(b.Y0 * scaleToOriginal),
            Width: (int)Math.Round((b.X1 - b.X0) * scaleToOriginal),
            Height: (int)Math.Round((b.Y1 - b.Y0) * scaleToOriginal)
        )).ToList();
    }

    /// <summary>Standard greedy IoU-based NMS, identical to FaceDetectionService.Nms including
    /// its "+1" area convention (matches deepghs' own _yolo_nms).</summary>
    private static List<(float X0, float Y0, float X1, float Y1, float Score)> Nms(
        List<(float X0, float Y0, float X1, float Y1, float Score)> boxes, float iouThreshold)
    {
        var order = boxes.Select((b, i) => i).OrderByDescending(i => boxes[i].Score).ToList();
        var suppressed = new bool[boxes.Count];
        var kept = new List<(float, float, float, float, float)>();

        foreach (int i in order)
        {
            if (suppressed[i]) continue;
            kept.Add(boxes[i]);
            foreach (int j in order)
            {
                if (j == i || suppressed[j]) continue;
                if (Iou(boxes[i], boxes[j]) > iouThreshold) suppressed[j] = true;
            }
        }
        return kept;
    }

    private static float Iou(
        (float X0, float Y0, float X1, float Y1, float Score) a,
        (float X0, float Y0, float X1, float Y1, float Score) b)
    {
        float ix1 = Math.Max(a.X0, b.X0), iy1 = Math.Max(a.Y0, b.Y0);
        float ix2 = Math.Min(a.X1, b.X1), iy2 = Math.Min(a.Y1, b.Y1);
        float iw = Math.Max(0, ix2 - ix1 + 1), ih = Math.Max(0, iy2 - iy1 + 1);
        float intersection = iw * ih;
        float areaA = (a.X1 - a.X0 + 1) * (a.Y1 - a.Y0 + 1);
        float areaB = (b.X1 - b.X0 + 1) * (b.Y1 - b.Y0 + 1);
        return intersection / (areaA + areaB - intersection);
    }
}

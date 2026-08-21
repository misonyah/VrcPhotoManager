using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace VrcPhotoManager.Services;

public record FaceBox(int X, int Y, int Width, int Height);

/// <summary>
/// Anime/stylized-face detector - deepghs/anime_face_detection's face_detect_v1.4_s (YOLOv8s,
/// ONNX, F1=0.95 per its own published metrics.json), replacing an earlier LBP cascade
/// (lbpcascade_animeface.xml, a 2013-era detector) after a real, measured comparison: a
/// close-up 3-person selfie with three clearly visible, well-lit, front-facing faces got only 1
/// detection from the LBP cascade - confirmed via its own returned box coordinates, it caught
/// the middle face and missed both side faces entirely. Tuning the LBP cascade's own
/// minNeighbors parameter down to 1 (its most lenient setting) didn't recover them either - it
/// just flooded the frame with false positives on background/ceiling/clothing while still
/// missing both real faces. The YOLOv8 model caught all 3 real faces at 0.85+ confidence, no
/// tuning needed.
///
/// Inference resolution matters a lot for this model: deepghs' own default preprocessing
/// squashes an image directly to the model's trained 640x640 size, no aspect-preserving resize
/// - fine for a single portrait, but on a real 7680x4320 30-person group photo that squash lost
/// so much resolution the model found ZERO faces (versus 15 from the old LBP cascade there).
/// Aspect-preserving resize (letterbox-padded to a multiple of 32, standard YOLO input prep) at
/// MaxInferSize=3200 recovered the same 15/15 count on that group photo while still catching all
/// 3 faces in the close-up case - the setting actually used here. Standard YOLOv8 output layout
/// verified directly against a live ONNX Runtime session (input 'images' [batch,3,H,W], output
/// 'output0' [batch, 4+numClasses, numAnchors], numClasses=1 here since this model only ever
/// predicts the single "face" label) rather than assumed blind.
/// </summary>
public class FaceDetectionService
{
    /// <summary>Longer side gets resized to at most this many pixels before padding to a
    /// multiple of Align - see the class doc comment for why smaller values silently miss faces
    /// on large group photos.</summary>
    private const int MaxInferSize = 3200;

    /// <summary>YOLOv8's fully-convolutional backbone needs input dimensions that are multiples
    /// of its total downsampling stride - 32 is standard for this architecture (confirmed via
    /// model_artifacts.json's yaml, five stride-2 Conv layers in the backbone).</summary>
    private const int Align = 32;

    /// <summary>deepghs' own published F1-optimal threshold for face_detect_v1.4_s specifically
    /// (metrics.json: F1=0.95 at this exact value) - reused rather than guessing, same spirit as
    /// CcipEmbeddingService reusing CCIP's own calibrated same-character threshold.</summary>
    private const float ConfThreshold = 0.307f;

    /// <summary>Standard YOLO NMS default - deepghs' own yolo_predict wrapper uses the same
    /// value.</summary>
    private const float IouThreshold = 0.7f;

    /// <summary>Padding color for the letterbox canvas - conventional YOLO mid-gray (114,114,114),
    /// matching deepghs' own preprocessing and standard Ultralytics letterbox behavior.</summary>
    private static readonly Scalar PadColor = new(114, 114, 114);

    private readonly InferenceSession _session;
    // Guards session.Run() only - see WdTaggerService for the underlying DirectML gotcha
    // (concurrent Run() calls on the same session caused a real native crash); preprocessing is
    // unaffected and safe to run concurrently across threads.
    private readonly object _inferenceLock = new();

    private FaceDetectionService(InferenceSession session)
    {
        _session = session;
    }

    public static FaceDetectionService? TryCreate(string modelDir, out string? error)
    {
        error = null;
        string modelPath = Path.Combine(modelDir, "model.onnx");
        if (!File.Exists(modelPath))
        {
            error = $"Face detection model not found at {modelDir} (expected model.onnx).";
            return null;
        }

        try
        {
            var options = new SessionOptions();
            try { options.AppendExecutionProvider_DML(); }
            catch { /* fall back to CPU EP silently if DirectML isn't available on this machine */ }
            return new FaceDetectionService(new InferenceSession(modelPath, options));
        }
        catch (Exception ex)
        {
            error = $"Failed to load face detection model: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Detects every anime-style face in the image. Unlike the Python reference-gathering
    /// scripts (identity_shortlist_v2.py), which only kept the largest face per image - fine for
    /// curating single-person reference sets - this returns every detected face, since group
    /// photos need all of them for VRCX-elimination labeling (Phase 2) to work.
    ///
    /// Throws if the image can't even be read (missing file, locked/partially-written file,
    /// corrupt data, or the classic OpenCV-on-Windows trap of a non-ASCII path silently failing
    /// Cv2.ImRead) - that case must be distinguishable from "zero faces found", since callers
    /// (FaceRepository.InsertDetectedFaces) delete existing rows before inserting new ones and
    /// would otherwise silently wipe out previously-correct detections on a bad re-scan.
    /// </summary>
    public List<FaceBox> DetectFaces(string imagePath)
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
            // .ToArray() copies out of the disposable result before the `using` block above
            // releases native memory backing it.
            var raw = results.First(r => r.Name == "output0").AsTensor<float>();
            outputTensor = new DenseTensor<float>(raw.ToArray(), raw.Dimensions.ToArray());
        }

        return Postprocess(outputTensor, scaleToOriginal);
    }

    /// <summary>Aspect-preserving resize (longer side to at most MaxInferSize) then right/bottom
    /// pad to a multiple of Align - standard YOLO "letterbox" input prep. Returns the padded
    /// tensor plus padW/padH (the ONNX input's actual dimensions - YOLOv8's fully-convolutional
    /// backbone accepts any size that's a multiple of Align, so this varies per image rather
    /// than being a fixed constant) and scaleToOriginal (multiply a model-space coordinate by
    /// this to map back to the source image - valid because the pre-pad resize preserved aspect
    /// ratio, so a single scalar works for both axes).</summary>
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

        // NCHW, normalized to [0,1] - no mean/std subtraction, matching deepghs' own rgb_encode
        // (plain /255.0, unlike CLIP-style models which subtract a dataset mean).
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
    /// width/height in padded-canvas pixel space, rows 4.. are one confidence score per class
    /// (just row 4 here, since this model only ever predicts "face"). Confidence-filters, then
    /// NMS, then maps surviving boxes back to original-image pixel coordinates.</summary>
    private static List<FaceBox> Postprocess(Tensor<float> output, double scaleToOriginal)
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
        return kept.Select(b => new FaceBox(
            X: (int)Math.Round(b.X0 * scaleToOriginal),
            Y: (int)Math.Round(b.Y0 * scaleToOriginal),
            Width: (int)Math.Round((b.X1 - b.X0) * scaleToOriginal),
            Height: (int)Math.Round((b.Y1 - b.Y0) * scaleToOriginal)
        )).ToList();
    }

    /// <summary>Standard greedy IoU-based NMS - highest-confidence box wins, anything
    /// sufficiently overlapping it gets suppressed, repeat. Matches deepghs' own _yolo_nms
    /// (imgutils/generic/yolo.py) exactly, including its "+1" area convention.</summary>
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

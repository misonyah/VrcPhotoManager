using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenCvSharp;

namespace VrcPhotoManager.Services;

public record FaceBox(int X, int Y, int Width, int Height);

/// <summary>
/// Anime/stylized-face detector (lbpcascade_animeface.xml, https://github.com/nagadomi/lbpcascade_animeface).
/// Standard face detectors trained on real photos (Haar cascades, InsightFace's RetinaFace/SCRFD)
/// miss VRChat avatars entirely - this LBP cascade was trained specifically for anime-style faces
/// and reliably detects them instead. See docs/superpowers/specs/2026-07-28-face-recognition-design.md.
/// </summary>
public class FaceDetectionService
{
    private readonly string _cascadePath;
    private readonly ThreadLocal<CascadeClassifier> _cascade;

    public FaceDetectionService()
    {
        _cascadePath = Path.Combine(AppContext.BaseDirectory, "Assets", "lbpcascade_animeface.xml");
        if (!File.Exists(_cascadePath))
        {
            throw new FileNotFoundException($"Anime face cascade not found at {_cascadePath}");
        }
        // CascadeClassifier is not safe to share across threads on Windows - same gotcha the
        // Python prototype hit, worked around there with threading.local().
        _cascade = new ThreadLocal<CascadeClassifier>(() => new CascadeClassifier(_cascadePath));
    }

    /// <summary>
    /// Wraps the constructor so a missing/corrupt cascade asset degrades to "face scanning
    /// unavailable" instead of bringing down whatever constructs this at startup (mirrors
    /// WdTaggerService.TryCreate).
    /// </summary>
    public static FaceDetectionService? TryCreate(out string? error)
    {
        error = null;
        try
        {
            return new FaceDetectionService();
        }
        catch (Exception ex)
        {
            error = ex.Message;
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

        using var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.EqualizeHist(gray, gray);

        var faces = _cascade.Value!.DetectMultiScale(
            gray, scaleFactor: 1.1, minNeighbors: 3, minSize: new Size(60, 60));

        return faces.Select(r => new FaceBox(r.X, r.Y, r.Width, r.Height)).ToList();
    }

}

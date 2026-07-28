using Microsoft.EntityFrameworkCore;
using VrcdnManager.Models;
using VrcdnManager.Services;

namespace VrcdnManager.Data;

public class FaceRepository(string dbPath)
{
    private VrcdnDbContext NewContext() => new(dbPath);

    /// <summary>
    /// Replaces this photo's previously-detected faces (re-scanning shouldn't accumulate
    /// duplicates - a photo re-scanned twice should still have exactly one row per real face).
    /// </summary>
    public void InsertDetectedFaces(long photoId, IEnumerable<FaceBox> faces)
    {
        using var context = NewContext();
        context.DetectedFaces.Where(f => f.PhotoId == photoId).ExecuteDelete();
        foreach (var f in faces)
        {
            context.DetectedFaces.Add(new DetectedFace
            {
                PhotoId = photoId,
                X = f.X,
                Y = f.Y,
                Width = f.Width,
                Height = f.Height,
            });
        }
        context.SaveChanges();
    }

    public Dictionary<long, int> GetFaceCountsByPhoto()
    {
        using var context = NewContext();
        return context.DetectedFaces
            .GroupBy(f => f.PhotoId)
            .Select(g => new { PhotoId = g.Key, Count = g.Count() })
            .ToDictionary(x => x.PhotoId, x => x.Count);
    }
}

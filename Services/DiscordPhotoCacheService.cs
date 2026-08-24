using System;
using System.IO;
using System.Linq;

namespace VrcPhotoManager.Services;

/// <summary>Owns the on-disk cache of downloaded Discord full-size originals and enforces the
/// configured size cap via two-tier LRU eviction. See PhotoSourceResolver for the download-on-
/// demand side of this - this class only handles the "where do cached files live" and "which
/// ones get deleted when over cap" concerns. See docs/superpowers/VrcPhotoManager/specs/
/// 2026-08-23-multi-library-discord-design.md's "Full-size cache + eviction" section.</summary>
public class DiscordPhotoCacheService(string cacheRootDir)
{
    public string GetCachePath(string remoteSourceId, string originalFilename)
    {
        // Defense in depth: callers are expected to pass a clean filename (no query string -
        // see PhotoSourceResolver.ResolveLocalPathAsync, which strips it via Uri.AbsolutePath
        // before calling here), but a raw Discord CDN URL's query string (?ex=...&hm=...) would
        // otherwise leak into Path.GetExtension's result and make File.WriteAllBytesAsync throw.
        int queryIndex = originalFilename.IndexOf('?');
        if (queryIndex >= 0) originalFilename = originalFilename[..queryIndex];

        string ext = Path.GetExtension(originalFilename);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        return Path.Combine(cacheRootDir, $"{remoteSourceId}{ext}");
    }

    /// <summary>Two-tier eviction: every fully-face-tagged cached photo is deleted (oldest-
    /// accessed first) before touching any not-fully-tagged one - see PhotoRepository.
    /// GetCachedDiscordPhotosForEviction for how "fully face-tagged" is determined. Only runs
    /// when the current total exceeds limitBytes; a no-op otherwise.</summary>
    public async Task EnforceCacheLimitAsync(Data.PhotoRepository photoRepo, long limitBytes)
    {
        var candidates = photoRepo.GetCachedDiscordPhotosForEviction();
        long currentTotal = candidates.Sum(c => c.FileSize);
        if (currentTotal <= limitBytes) return;

        var evictionOrder = candidates
            .OrderByDescending(c => c.FullyFaceTagged) // fully-tagged (true) evicted first
            .ThenBy(c => c.LastAccessedAt ?? DateTime.MinValue)
            .ToList();

        foreach (var candidate in evictionOrder)
        {
            if (currentTotal <= limitBytes) break;
            try
            {
                if (File.Exists(candidate.LocalPath))
                {
                    var info = new FileInfo(candidate.LocalPath);
                    await Task.Run(() => File.Delete(candidate.LocalPath));
                    currentTotal -= info.Length;
                }
                photoRepo.ClearLocalPath(candidate.PhotoId);
            }
            catch (IOException)
            {
                // File locked/in use elsewhere - skip it this pass, try again on the next
                // eviction check rather than failing the whole enforcement run over one file.
            }
        }
    }
}

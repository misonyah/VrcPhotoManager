using System.IO;
using System.Linq;
using System.Net.Http;

namespace VrcPhotoManager.Services;

/// <summary>
/// Downloads the WD14 tagger's two required files (model.onnx, ~378MB; selected_tags.csv,
/// ~300KB) directly from Hugging Face. Model confirmed from the existing Python tooling's
/// own docstring and download cache (tag_photos.py: "SmilingWolf's wd-vit-tagger-v3") -
/// not guessed. Public "resolve/main" URLs work for anonymous, unauthenticated downloads
/// of this public repo, same as a browser would fetch.
/// </summary>
public class ModelDownloadService
{
    private const string ModelRepoBaseUrl = "https://huggingface.co/SmilingWolf/wd-vit-tagger-v3/resolve/main";
    private const string ClipModelRepoBaseUrl = "https://huggingface.co/immich-app/ViT-L-14__laion2b-s32b-b82k/resolve/main/visual";
    private const string AvatarModelRepoBaseUrl = "https://huggingface.co/misonyah/vrc-avatar-classifier/resolve/main";

    public async Task DownloadWdTaggerModelAsync(string targetDir, IProgress<string> progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        await DownloadFileAsync(http, $"{ModelRepoBaseUrl}/model.onnx", Path.Combine(targetDir, "model.onnx"), "model.onnx", progress, ct);
        await DownloadFileAsync(http, $"{ModelRepoBaseUrl}/selected_tags.csv", Path.Combine(targetDir, "selected_tags.csv"), "selected_tags.csv", progress, ct);
    }

    public async Task DownloadClipModelAsync(string targetDir, IProgress<string> progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        await DownloadFileAsync(http, $"{ClipModelRepoBaseUrl}/model.onnx", Path.Combine(targetDir, "model.onnx"), "model.onnx", progress, ct);
    }

    public async Task DownloadAvatarModelAsync(string targetDir, IProgress<string> progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        await DownloadFileAsync(http, $"{AvatarModelRepoBaseUrl}/model.onnx", Path.Combine(targetDir, "model.onnx"), "model.onnx", progress, ct);
        await DownloadFileAsync(http, $"{AvatarModelRepoBaseUrl}/labels.txt", Path.Combine(targetDir, "labels.txt"), "labels.txt", progress, ct);
    }

    public Task<string?> GetRemoteWdTaggerModelETagAsync(CancellationToken ct = default) =>
        GetRemoteETagAsync($"{ModelRepoBaseUrl}/model.onnx", ct);

    public Task<string?> GetRemoteClipModelETagAsync(CancellationToken ct = default) =>
        GetRemoteETagAsync($"{ClipModelRepoBaseUrl}/model.onnx", ct);

    public Task<string?> GetRemoteAvatarModelETagAsync(CancellationToken ct = default) =>
        GetRemoteETagAsync($"{AvatarModelRepoBaseUrl}/model.onnx", ct);

    /// <summary>Cheap version check for a model: a HEAD request against the given
    /// "resolve/main" file URL with redirects disabled, reading the content-hash ETag
    /// Hugging Face returns on the redirect response itself (verified against a real
    /// fetch: both an LFS-tracked model.onnx and a plain-text labels.txt return an
    /// "X-Linked-Etag" header on the 302/307 response, before it's followed to the
    /// actual CDN URL - no need to download anything to get it). Falls back to the
    /// standard ETag header if that's ever absent. Returns null if neither header is
    /// present or the request fails - callers should treat that as "can't tell,
    /// download anyway" rather than an error. Each of the three model files is
    /// published together in one run, so checking the primary model.onnx alone is
    /// representative of the whole set.</summary>
    private static async Task<string?> GetRemoteETagAsync(string fileUrl, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, fileUrl);
            using var response = await http.SendAsync(request, ct);
            if (response.Headers.TryGetValues("X-Linked-Etag", out var linkedValues))
            {
                return linkedValues.FirstOrDefault();
            }
            return response.Headers.ETag?.Tag;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static async Task DownloadFileAsync(
        HttpClient http, string url, string destPath, string label, IProgress<string> progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long? totalBytes = response.Content.Headers.ContentLength;
        string totalText = totalBytes is long total ? $"{total / 1024.0 / 1024.0:N1} MB" : "unknown size";

        await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long readSoFar = 0;
        long lastReportedMb = -1;
        int bytesRead;
        while ((bytesRead = await httpStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            readSoFar += bytesRead;

            // Throttle to roughly once per MB - a 378MB file at the raw buffer size would
            // otherwise post several thousand progress updates.
            long currentMb = readSoFar / (1024 * 1024);
            if (currentMb != lastReportedMb)
            {
                lastReportedMb = currentMb;
                progress.Report($"Downloading {label}: {readSoFar / 1024.0 / 1024.0:N1} MB / {totalText}");
            }
        }

        progress.Report($"{label} done ({readSoFar / 1024.0 / 1024.0:N1} MB).");
    }
}

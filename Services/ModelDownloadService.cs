using System.IO;
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

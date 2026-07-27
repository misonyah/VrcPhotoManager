using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VrcdnManager.Services;

public record RemoteObject(
    [property: JsonPropertyName("Id")] string Id,
    [property: JsonPropertyName("Extension")] string Extension,
    [property: JsonPropertyName("Original")] string Original,
    [property: JsonPropertyName("Type")] string Type,
    [property: JsonPropertyName("Size")] long Size);

public record ActiveJob(
    [property: JsonPropertyName("Id")] string Id,
    [property: JsonPropertyName("Original")] string Original,
    [property: JsonPropertyName("Status")] string Status);

public record QuotaInfo(
    [property: JsonPropertyName("QuotaUsed")] long QuotaUsed,
    [property: JsonPropertyName("Quota")] long Quota);

/// <summary>
/// Re-implements the reverse-engineered panel.vrcdn.live upload flow (see the
/// vrcdn-photo-upload skill). This is not a published API - be a good citizen: sequential
/// requests, small delay between uploads, presigned URL requested immediately before each
/// PUT rather than batched upfront.
/// </summary>
public class VrcdnApiClient
{
    private const string Base = "https://panel.vrcdn.live";
    private readonly HttpClient _http;

    public VrcdnApiClient(string sessionCookie)
    {
        var handler = new HttpClientHandler { UseCookies = false };
        _http = new HttpClient(handler) { BaseAddress = new Uri(Base) };
        _http.DefaultRequestHeaders.Add("Cookie", $"PHPSESSID={sessionCookie}");
        _http.DefaultRequestHeaders.Add("Referer", $"{Base}/obj-upload.php");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) VrcdnManager/1.0");
    }

    private async Task<JsonDocument> PostAsync(object body, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync("/parts/s3.funcs.php", body, ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    public async Task<QuotaInfo> GetQuotaAsync(CancellationToken ct = default)
    {
        using var doc = await PostAsync(new { type = "getQuota" }, ct);
        return doc.Deserialize<QuotaInfo>()!;
    }

    public async Task<List<RemoteObject>> ListObjectsAsync(CancellationToken ct = default)
    {
        using var doc = await PostAsync(new { type = "list", listType = "ListObjects" }, ct);
        return doc.Deserialize<List<RemoteObject>>() ?? [];
    }

    public async Task<List<ActiveJob>> ListActiveJobsAsync(CancellationToken ct = default)
    {
        using var doc = await PostAsync(new { type = "list", listType = "ActiveJobs" }, ct);
        return doc.Deserialize<List<ActiveJob>>() ?? [];
    }

    public async Task RemoveObjectAsync(string objectId, CancellationToken ct = default)
    {
        using var doc = await PostAsync(new { type = "removeObject", objectId }, ct);
    }

    /// <summary>
    /// Uploads one file: requests a presigned URL, then PUTs the bytes with a matching
    /// Content-Type header. The Content-Type header on the PUT is the one gotcha that
    /// silently fails uploads - S3 returns 200 regardless, but the backend job processor
    /// marks it "Failed" without it.
    /// </summary>
    public async Task<string> UploadFileAsync(string localPath, CancellationToken ct = default)
    {
        var info = new FileInfo(localPath);
        string contentType = Path.GetExtension(localPath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };

        using var presignDoc = await PostAsync(new
        {
            type = "upload",
            fileName = info.Name,
            fileType = contentType,
            fileSize = info.Length,
        }, ct);

        var root = presignDoc.RootElement;
        if (!root.GetProperty("Success").GetBoolean())
        {
            string message = root.TryGetProperty("Message", out var m) ? m.GetString() ?? "" : "";
            throw new InvalidOperationException($"Presign request failed: {message}");
        }

        string url = root.GetProperty("Url").GetString()!;
        string jobId = root.GetProperty("JobId").GetString()!;

        using var fileStream = File.OpenRead(localPath);
        using var content = new StreamContent(fileStream);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var putResp = await new HttpClient().PutAsync(url, content, ct);
        putResp.EnsureSuccessStatusCode();

        return jobId;
    }
}

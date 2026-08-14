using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VrcPhotoManager.Services;

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
    private string? _cachedUsername;

    public VrcdnApiClient(string sessionCookie)
    {
        var handler = new HttpClientHandler { UseCookies = false };
        _http = new HttpClient(handler) { BaseAddress = new Uri(Base) };
        _http.DefaultRequestHeaders.Add("Cookie", $"PHPSESSID={sessionCookie}");
        _http.DefaultRequestHeaders.Add("Referer", $"{Base}/obj-upload.php");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) VrcPhotoManager/1.0");
    }

    private async Task<JsonDocument> PostAsync(object body, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync("/parts/s3.funcs.php", body, ct);
        resp.EnsureSuccessStatusCode();

        string text = await resp.Content.ReadAsStringAsync(ct);
        // An expired/invalid session gets redirected to an HTML login page (still 200 OK) rather
        // than an error status - fail with a clear message instead of a cryptic JSON parse error.
        if (resp.Content.Headers.ContentType?.MediaType != "application/json")
        {
            throw new InvalidOperationException(
                "VRCDN session expired or invalid - log in again.");
        }
        return JsonDocument.Parse(text);
    }

    /// <summary>
    /// There's no API for this - the served-file username is embedded as `const userName =
    /// "..."` in obj-files.php's own inline script (VRCDN's page templates it in server-side
    /// per logged-in account). Scraped once and cached rather than hardcoded, since it's
    /// different for every VRCDN account.
    /// </summary>
    public async Task<string> GetUsernameAsync(CancellationToken ct = default)
    {
        if (_cachedUsername is not null) return _cachedUsername;

        string html = await _http.GetStringAsync("/obj-files.php", ct);
        var match = Regex.Match(html, """const userName = "([^"]+)""");
        if (!match.Success)
        {
            // The overwhelmingly likely cause is an expired/invalid session getting
            // redirected to the login page instead of the real obj-files.php content -
            // same class of issue as the earlier login bugs. A genuine page-layout change
            // is possible but much rarer; lead with the actionable explanation either way.
            bool looksLikeLoginRedirect = html.Contains("id.vrcdn.live", StringComparison.OrdinalIgnoreCase)
                || html.Contains("Account/Login", StringComparison.OrdinalIgnoreCase);
            throw new InvalidOperationException(looksLikeLoginRedirect
                ? "Session appears to be expired or invalid - log in again."
                : "Could not determine VRCDN username from obj-files.php - your session may be expired (try logging in again), or VRCDN's page layout may have changed.");
        }

        _cachedUsername = match.Groups[1].Value;
        return _cachedUsername;
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
    /// Uploads pre-processed bytes (already resized to fit VRChat's 2048x2048 image-loader
    /// cap - see ThumbnailService.PrepareForUploadAsync): requests a presigned URL, then
    /// PUTs the bytes with a matching Content-Type header. The Content-Type header on the
    /// PUT is the one gotcha that silently fails uploads - S3 returns 200 regardless, but
    /// the backend job processor marks it "Failed" without it. contentType defaults to
    /// "image/jpeg" for the common photo-upload case (PrepareForUploadAsync always re-encodes
    /// as JPEG) - MainViewModel.GenerateVrcdnIndexAsync passes a text/csv, application/json, or
    /// text/plain override for the index file instead.
    /// </summary>
    public async Task<string> UploadBytesAsync(string fileName, byte[] bytes, string contentType = "image/jpeg", CancellationToken ct = default)
    {
        using var presignDoc = await PostAsync(new
        {
            type = "upload",
            fileName,
            fileType = contentType,
            fileSize = bytes.LongLength,
        }, ct);

        var root = presignDoc.RootElement;
        if (!root.GetProperty("Success").GetBoolean())
        {
            string message = root.TryGetProperty("Message", out var m) ? m.GetString() ?? "" : "";
            throw new InvalidOperationException($"Presign request failed: {message}");
        }

        string url = root.GetProperty("Url").GetString()!;
        string jobId = root.GetProperty("JobId").GetString()!;

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var putResp = await new HttpClient().PutAsync(url, content, ct);
        putResp.EnsureSuccessStatusCode();

        return jobId;
    }
}

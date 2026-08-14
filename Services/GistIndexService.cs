using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace VrcPhotoManager.Services;

/// <summary>Minimal wrapper around GitHub's Gist REST API for the VRCDN photo-index feature
/// (see MainViewModel.UpdateVrcdnIndexAsync) - creates a secret gist once, then updates it in
/// place (PATCH) on every later regeneration, giving a stable gist.githubusercontent.com raw URL
/// a Udon world script can hardcode permanently (it's on VRChat's Udon string-loading trusted-
/// domain allowlist - see creators.vrchat.com/worlds/udon/external-urls). Needs only the narrow
/// "gist" OAuth scope on the token - no repository access, no repository created.</summary>
public class GistIndexService
{
    private readonly HttpClient _http;

    public GistIndexService(string token)
    {
        _http = new HttpClient { BaseAddress = new Uri("https://api.github.com") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        // Required by GitHub's API for all requests - any identifying value is accepted.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VrcPhotoManager");
    }

    /// <summary>Creates a new secret (unlisted, but publicly fetchable by URL) gist and returns
    /// its id plus the STABLE raw URL - gist.githubusercontent.com/{login}/{id}/raw/{fileName}
    /// with no revision hash always serves the latest content, unlike the revision-specific
    /// raw_url GitHub's own API response includes.</summary>
    public async Task<(string GistId, string RawUrl)> CreateGistAsync(
        string fileName, string content, string description, CancellationToken ct = default)
    {
        var body = new
        {
            description,
            @public = false,
            files = new Dictionary<string, object> { [fileName] = new { content } },
        };
        using var resp = await _http.PostAsJsonAsync("/gists", body, ct);
        await EnsureSuccessAsync(resp, ct);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        string gistId = doc.RootElement.GetProperty("id").GetString()!;
        string login = doc.RootElement.GetProperty("owner").GetProperty("login").GetString()!;
        return (gistId, $"https://gist.githubusercontent.com/{login}/{gistId}/raw/{fileName}");
    }

    /// <summary>Updates an existing gist's file content in place - the gist id and raw URL
    /// (see CreateGistAsync) never change from this.</summary>
    public async Task UpdateGistAsync(string gistId, string fileName, string content, CancellationToken ct = default)
    {
        var body = new { files = new Dictionary<string, object> { [fileName] = new { content } } };
        using var resp = await _http.PatchAsJsonAsync($"/gists/{gistId}", body, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        string text = await resp.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"GitHub Gist API error ({(int)resp.StatusCode}): {text[..Math.Min(300, text.Length)]}");
    }
}

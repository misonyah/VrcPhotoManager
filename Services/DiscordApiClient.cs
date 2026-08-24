using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VrcPhotoManager.Services;

public record DiscordGuild(string Id, string Name);
public record DiscordChannel(string Id, string Name);
public record DiscordAttachment(string Url, string Filename, [property: JsonPropertyName("content_type")] string? ContentType);
public record DiscordMessage(string Id, List<DiscordAttachment> Attachments);

/// <summary>Minimal hand-rolled Discord REST client (bot-token auth, message-history pagination
/// only) - deliberately not a full Discord library (Discord.Net etc.) since this app needs none
/// of the gateway/voice/interaction surface those bring, only a handful of REST endpoints. See
/// docs/superpowers/VrcPhotoManager/specs/2026-08-23-multi-library-discord-design.md's "Discord:
/// setup UX" and "Discord: sync mechanism" sections.
///
/// Handles Discord's structured REST rate limiting (429 + Retry-After header) by waiting and
/// retrying once - this is a documented, mechanical contract (unlike CDN throttling, which isn't
/// officially documented and is instead handled by simple request pacing in
/// DiscordLibraryService, not here).</summary>
public class DiscordApiClient : IDisposable
{
    private const string ApiBase = "https://discord.com/api/v10";
    private readonly HttpClient _http;

    public DiscordApiClient(string botToken)
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", botToken);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VrcPhotoManager (https://github.com/misonyah/VrcPhotoManager, 1.0)");
    }

    private async Task<HttpResponseMessage> GetWithRateLimitRetryAsync(string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            double retryAfterSeconds = 1.0;
            if (response.Headers.TryGetValues("Retry-After", out var values)
                && double.TryParse(values.FirstOrDefault(), out double parsed))
            {
                retryAfterSeconds = parsed;
            }
            await Task.Delay(TimeSpan.FromSeconds(retryAfterSeconds), ct);
            response = await _http.GetAsync(url, ct);
        }
        response.EnsureSuccessStatusCode();
        return response;
    }

    public async Task<List<DiscordGuild>> GetGuildsAsync(CancellationToken ct)
    {
        using var response = await GetWithRateLimitRetryAsync($"{ApiBase}/users/@me/guilds", ct);
        var raw = await response.Content.ReadFromJsonAsync<List<JsonElement>>(cancellationToken: ct) ?? [];
        return raw.Select(g => new DiscordGuild(
            g.GetProperty("id").GetString()!,
            g.GetProperty("name").GetString()!
        )).ToList();
    }

    public async Task<List<DiscordChannel>> GetChannelsAsync(string guildId, CancellationToken ct)
    {
        using var response = await GetWithRateLimitRetryAsync($"{ApiBase}/guilds/{guildId}/channels", ct);
        var raw = await response.Content.ReadFromJsonAsync<List<JsonElement>>(cancellationToken: ct) ?? [];
        // type 0 = GUILD_TEXT - the only channel type this app can read messages/attachments from.
        return raw.Where(c => c.GetProperty("type").GetInt32() == 0)
            .Select(c => new DiscordChannel(c.GetProperty("id").GetString()!, c.GetProperty("name").GetString()!))
            .ToList();
    }

    /// <summary>One page (up to 100 messages). Discord's `after` param returns messages newer
    /// than the given id, oldest-first isn't native to the API - the caller (DiscordLibraryService)
    /// is responsible for walking pages forward via each page's last message id, matching the
    /// design spec's "resuming from LastSyncedMessageId" pagination approach.</summary>
    public async Task<List<DiscordMessage>> GetMessagesAsync(string channelId, string? afterMessageId, CancellationToken ct)
    {
        string url = $"{ApiBase}/channels/{channelId}/messages?limit=100";
        if (afterMessageId is not null) url += $"&after={afterMessageId}";

        using var response = await GetWithRateLimitRetryAsync(url, ct);
        var messages = await response.Content.ReadFromJsonAsync<List<DiscordMessage>>(cancellationToken: ct) ?? [];
        // Discord returns newest-first regardless of `after` - reverse so callers can walk
        // oldest-to-newest and use the LAST item's id as the next page's `after` cursor.
        messages.Reverse();
        return messages;
    }

    public void Dispose() => _http.Dispose();
}

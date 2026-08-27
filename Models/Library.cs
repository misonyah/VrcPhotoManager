namespace VrcPhotoManager.Models;

public enum LibraryType
{
    LocalFolder,
    DiscordChannel,
}

/// <summary>A source of photos - a local folder or a Discord channel "virtual library". Every
/// Photo belongs to exactly one Library (Photo.LibraryId). See docs/superpowers/VrcPhotoManager/
/// specs/2026-08-23-multi-library-discord-design.md for the full design.</summary>
public class Library
{
    public long Id { get; set; }
    public LibraryType Type { get; set; }
    public required string DisplayName { get; set; }

    /// <summary>Set when Type == LocalFolder.</summary>
    public string? LocalPath { get; set; }

    /// <summary>Set when Type == DiscordChannel - for display only (which server).</summary>
    public string? DiscordGuildId { get; set; }

    /// <summary>Discord-only, resolved once when the channel is added (see
    /// DiscordApiClient.GetGuildsAsync's DiscordGuild.IconUrl) - a CDN URL, or null if the
    /// guild has no custom icon set. Displayed as a small badge on that library's photo
    /// thumbnails (see PhotoViewModel.DiscordGuildIcon) so photos from different servers are
    /// visually distinguishable at a glance.</summary>
    public string? DiscordGuildIconUrl { get; set; }

    /// <summary>Set when Type == DiscordChannel.</summary>
    public string? DiscordChannelId { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    /// <summary>Discord-only: pagination cursor (a Discord message id) for incremental sync -
    /// the next sync resumes with ?after=this value instead of re-fetching full history.</summary>
    public string? LastSyncedMessageId { get; set; }

    /// <summary>Discord-only, default false. When false, batch operations (Detect Faces,
    /// Classify Avatars, Suggest Faces, Classify Photos) only process already-cached Discord
    /// photos instead of downloading everything needed - see PhotoSourceResolver callers in
    /// Part 2.</summary>
    public bool AutoDownloadOriginals { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Default true. When false, this library is fully paused: its photos are hidden
    /// from the main grid, excluded from every batch operation's candidate set (Detect Faces,
    /// Classify Avatars, Suggest Faces, Classify Photos - see MainViewModel.
    /// IsEligibleForBatchOperation), and skipped during Scan Libraries - without removing the
    /// library or orphaning its already-scanned photos the way outright removal does. Toggled
    /// via a checkbox in Settings' Library tab.</summary>
    public bool Enabled { get; set; } = true;
}

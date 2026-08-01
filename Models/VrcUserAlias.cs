namespace VrcPhotoManager.Models;

public enum VrcUserAliasSource
{
    /// <summary>Typed in by hand via the Tag Faces picker's alias editor - never
    /// auto-evicted when a user's alias count would exceed the cap.</summary>
    Manual,

    /// <summary>Captured automatically from VRCX's own rename history (friend_log_history's
    /// explicit DisplayName events, or distinct names seen for the same user id in
    /// gamelog_join_leave) - the oldest of these is evicted first if a user's alias count
    /// would exceed the cap.</summary>
    History,
}

/// <summary>
/// A past or alternate name for a VRC user id, searchable in the Tag Faces picker even
/// before that person has ever been tagged (no FK to RegisteredPerson - a real example that
/// prompted this: "computerfreaker" wasn't found searching "Emillia", one of their prior
/// names, and wasn't a registered person at all yet). Capped at 6 per user id - see
/// FaceRepository.AddOrCaptureAlias for the eviction rule.
/// </summary>
public class VrcUserAlias
{
    public long Id { get; set; }
    public required string UserId { get; set; }
    public required string Alias { get; set; }
    public VrcUserAliasSource Source { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

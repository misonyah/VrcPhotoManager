namespace VrcPhotoManager.Models;

/// <summary>
/// A local, permanent cache of every VRC user id + display name this app has ever seen via
/// VRCX (friends list or gamelog), independent of VRCX's own data - VRCX's gamelog can be
/// cleared or rotated, and a friend can be removed, either of which would otherwise silently
/// regress the Tag Faces "who is this" autocomplete for anyone only known that way. Opportunistically
/// upserted every time Tag Faces opens (see TagFacesWindow's constructor); never the primary
/// source (live VRCX data always wins when available), just a fallback that survives VRCX's
/// own data going away later.
/// </summary>
public class KnownVrcUser
{
    public required string UserId { get; set; }
    public required string DisplayName { get; set; }
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

namespace VrcPhotoManager.Models;

public class RegisteredPerson
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Null when a person was created via free-text tagging with no known VRC
    /// account (see FaceRepository.CreatePerson). When set, this is what links a tag back to
    /// a real VRChat identity - used by the player-filter "(tagged)" annotation, the "Tagged
    /// only" checkbox, and the VRCX profile-picture bootstrap.</summary>
    public string? VrcUserId { get; set; }

    /// <summary>Best-effort thumbnail fetched from VRCX's own locally-cached avatar-change
    /// feed the first time this person is linked to a VrcUserId - see
    /// VrcxProfileLookupService. Null if never fetched or the fetch failed; that's a normal,
    /// silent outcome, not an error state.</summary>
    public byte[]? VrcProfileThumbnail { get; set; }
    public DateTime? VrcProfileThumbnailFetchedAt { get; set; }
}

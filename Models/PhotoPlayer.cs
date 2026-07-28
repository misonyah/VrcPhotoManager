namespace VrcdnManager.Models;

/// <summary>
/// One row per player VRCX recorded as present when a photo was taken - both the stable
/// VRChat user id and the display name at capture time. Display names can change over time
/// (renames); the id doesn't, so future features (e.g. matching a person across photos)
/// should key off UserId, not DisplayName. Read from the same PNG metadata PngMetadataReader
/// already parses - this just persists the id half that was previously discarded.
/// </summary>
public class PhotoPlayer
{
    public long Id { get; set; }
    public long PhotoId { get; set; }
    public required string UserId { get; set; }
    public required string DisplayName { get; set; }
}

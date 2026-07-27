namespace VrcdnManager.Models;

public enum RemoteStatus
{
    NotUploaded,
    Uploading,
    Uploaded,
    Failed,
}

public class Photo
{
    public long Id { get; set; }
    public required string LocalPath { get; set; }
    public long FileSize { get; set; }
    public double Mtime { get; set; }
    public bool HasThumbnail { get; set; }
    public string? Rating { get; set; }
    public bool Selected { get; set; }
    public RemoteStatus RemoteStatus { get; set; } = RemoteStatus.NotUploaded;
    public string? RemoteUrl { get; set; }
    public string? RemoteId { get; set; }
    public string? UploadedAt { get; set; }

    public string FileName => System.IO.Path.GetFileName(LocalPath);
}

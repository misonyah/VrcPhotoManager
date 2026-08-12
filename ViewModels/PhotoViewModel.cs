using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VrcPhotoManager.Data;
using VrcPhotoManager.Models;
using VrcPhotoManager.Services;

namespace VrcPhotoManager.ViewModels;

public class PhotoViewModel : INotifyPropertyChanged
{
    public Photo Model { get; }
    private readonly PhotoRepository _repo;

    private BitmapImage? _thumbnail;
    private bool _thumbnailLoadAttempted;

    /// <summary>Right-click "Rating" submenu - CommandParameter is the rating string, or
    /// null to clear it back to unclassified.</summary>
    public ICommand SetRatingCommand { get; }

    public PhotoViewModel(Photo model, PhotoRepository repo)
    {
        Model = model;
        _repo = repo;
        SetRatingCommand = new RelayCommand<string>(rating =>
        {
            Model.Rating = rating;
            _repo.SetRating(Model.Id, rating);
            NotifyRatingChanged();
        });
    }

    public string FileName => Model.FileName;
    public string? Rating => Model.Rating;
    public string? AvatarType => Model.AvatarType;
    public RemoteStatus RemoteStatus => Model.RemoteStatus;
    public string? RemoteUrl => Model.RemoteUrl;
    public string? UploadCropMode => Model.UploadCropMode;
    public double CropOffsetX => Model.CropOffsetX;
    public double CropOffsetY => Model.CropOffsetY;
    public string? CropRatioOverride => Model.CropRatioOverride;

    /// <summary>Per-keypress nudge amount - a -1..1 fraction of the crop's available slack per
    /// axis, so 50 presses moves the crop from centered to a pinned edge. 0.1 (10 presses full
    /// range) was too coarse for real use - a single tap moved the crop by a visually large
    /// jump, per a real report.</summary>
    private const double CropNudgeStep = 0.02;

    /// <summary>Raised when adjusting the crop on an already-Uploaded photo reverts it back to
    /// NotUploaded (see RevertForRecrop) - MainWindow uses this to surface a status message,
    /// since the cloud/uploaded badge quietly disappearing with no other explanation would be
    /// confusing on its own.</summary>
    public event EventHandler? RevertedForRecrop;

    /// <summary>Adjusting an already-Uploaded photo's crop only makes sense as "I want to
    /// re-crop and re-upload this" - the crop that's actually live on VRCDN can't be changed in
    /// place. Reverting to NotUploaded (mirroring RemoveFromVrcdnAsync's own ClearRemoteStatus
    /// call, though this doesn't touch VRCDN itself - only local tracking) makes the photo
    /// eligible for Upload Selected again and switches the preview overlay back to showing the
    /// pending edit instead of the old live crop. A previous version just silently blocked any
    /// adjustment once Uploaded, which read as "the keys stopped working" rather than "you need
    /// to re-upload to change this" - now the very act of adjusting IS how you start doing
    /// that.</summary>
    private void RevertForRecrop()
    {
        Model.RemoteStatus = RemoteStatus.NotUploaded;
        Model.RemoteUrl = null;
        Model.RemoteId = null;
        Model.UploadedAt = null;
        Model.UploadCropMode = null;
        _repo.ClearRemoteStatus(Model.Id);
        RefreshStatus();
        RevertedForRecrop?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adjusts where the upload crop sits within the source image (see
    /// Photo.CropOffsetX's doc comment) - called from MainWindow's PreviewKeyDown while this
    /// photo is the one currently hovered. Reverts an already-Uploaded photo back to
    /// NotUploaded first (see RevertForRecrop) rather than no-opping.</summary>
    public void NudgeCropOffset(int dx, int dy)
    {
        if (Model.RemoteStatus == RemoteStatus.Uploaded) RevertForRecrop();

        double newX = Math.Clamp(Model.CropOffsetX + dx * CropNudgeStep, -1, 1);
        double newY = Math.Clamp(Model.CropOffsetY + dy * CropNudgeStep, -1, 1);
        if (newX == Model.CropOffsetX && newY == Model.CropOffsetY) return;

        Model.CropOffsetX = newX;
        Model.CropOffsetY = newY;
        _repo.SetCropOffset(Model.Id, newX, newY);
        OnPropertyChanged(nameof(CropOffsetX));
        OnPropertyChanged(nameof(CropOffsetY));
    }

    /// <summary>Cycles this photo's per-photo crop-ratio override forward (direction=+1) or
    /// backward (direction=-1) through the same fixed presets as the global upload-crop
    /// dropdown, skipping "Custom..." (a keyboard cycle can't usefully drive its free-text
    /// ratio). Called from MainWindow's PreviewKeyDown ([ / ] keys) while this photo is the one
    /// currently hovered - see Photo.CropRatioOverride's doc comment. Cycling wraps through a
    /// null "use the dropdown" state between the last and first preset, rather than skipping
    /// straight from last to first, so there's always an easy way back to "just use the
    /// dropdown" without having to know which preset that currently is. Reverts an already-
    /// Uploaded photo back to NotUploaded first (see RevertForRecrop) rather than no-opping.</summary>
    public void CycleCropRatioOverride(int direction, IReadOnlyList<MainViewModel.UploadCropPreset> presets)
    {
        if (Model.RemoteStatus == RemoteStatus.Uploaded) RevertForRecrop();

        var cyclable = presets.Where(p => !p.IsCustom).ToList();
        if (cyclable.Count == 0) return;

        // States: 0 = null ("use the dropdown"), 1..cyclable.Count = cyclable[state - 1].
        int totalStates = cyclable.Count + 1;
        // FindIndex returns -1 for a stale/unrecognized label (e.g. a preset removed since it
        // was set), which +1 conveniently also lands on 0 - the same "treat as null" state.
        int currentState = Model.CropRatioOverride is null
            ? 0
            : cyclable.FindIndex(p => p.Name == Model.CropRatioOverride) + 1;
        int nextState = ((currentState + direction) % totalStates + totalStates) % totalStates;

        string? newOverride = nextState == 0 ? null : cyclable[nextState - 1].Name;
        if (newOverride == Model.CropRatioOverride) return;

        Model.CropRatioOverride = newOverride;
        _repo.SetCropRatioOverride(Model.Id, newOverride);
        OnPropertyChanged(nameof(CropRatioOverride));
    }

    /// <summary>UploadCropMode trimmed to just the ratio (e.g. "4:3" out of "4:3 (Landscape)") -
    /// the full label is still available via the badge's ToolTip.</summary>
    public string? UploadCropModeShort => Model.UploadCropMode is string mode ? CropRatioLabels.ShortLabel(mode) : null;
    public string? AuthorDisplayName => Model.AuthorDisplayName;
    public string? WorldName => Model.WorldName;

    /// <summary>Built on demand from the real PhotoPlayer/GamelogInferredPlayer rows -
    /// PhotoRepository's actual relational source of truth for a photo's players (see
    /// SetVrcxMetadata/InsertGamelogInferredPlayers), rather than a flattened string column.
    /// Queried live rather than cached since this only runs once per hover-preview pop-up, not
    /// per photo in the grid.</summary>
    public string PlayersTooltip
    {
        get
        {
            if (!Model.MetadataScanned) return "Not scanned yet";

            var players = _repo.GetPlayersForPhoto(Model.Id);
            IEnumerable<string> names = players.Count > 0
                ? players.Select(p => p.DisplayName)
                : _repo.GetGamelogInferredPlayersForPhoto(Model.Id).Select(p => p.DisplayName);

            string joined = string.Join(", ", names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            return joined.Length == 0 ? "No VRCX metadata" : $"{Model.WorldName}\nPlayers: {joined}";
        }
    }

    /// <summary>Whether VRCX embedded any real metadata for this photo - drives the small
    /// people-icon badge on the thumbnail. Not the same signal as DetectedFaceCount (that's
    /// computer-vision face detection; this is VRCX's own author/player tagging).</summary>
    public bool HasVrcxMetadata => Model.AuthorDisplayName is not null;

    /// <summary>Raised when Selected changes, so MainViewModel can re-evaluate the
    /// Upload/Remove-from-VRCDN commands' enabled state without polling every photo.</summary>
    public event EventHandler? SelectionChanged;

    public bool Selected
    {
        get => Model.Selected;
        set
        {
            if (Model.Selected == value) return;
            Model.Selected = value;
            OnPropertyChanged();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>VRCX-recorded world-instance player count (not detected-face count) - drives
    /// the "People in world" filter.</summary>
    private int _worldPlayerCount;
    public int WorldPlayerCount
    {
        get => _worldPlayerCount;
        set
        {
            if (_worldPlayerCount == value) return;
            _worldPlayerCount = value;
            OnPropertyChanged();
        }
    }

    private static readonly SolidColorBrush AllTaggedBadgeBrush = CreateFrozenBrush(0xCC, 0x1B, 0x5E, 0x20);
    private static readonly SolidColorBrush DefaultBadgeBrush = CreateFrozenBrush(0xCC, 0x00, 0x00, 0x00);

    private static SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private int _detectedFaceCount;
    public int DetectedFaceCount
    {
        get => _detectedFaceCount;
        set
        {
            if (_detectedFaceCount == value) return;
            _detectedFaceCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FaceCountDisplay));
            OnPropertyChanged(nameof(FaceCountBadgeBrush));
        }
    }

    private int _taggedFaceCount;
    public int TaggedFaceCount
    {
        get => _taggedFaceCount;
        set
        {
            if (_taggedFaceCount == value) return;
            _taggedFaceCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FaceCountDisplay));
            OnPropertyChanged(nameof(FaceCountBadgeBrush));
        }
    }

    /// <summary>"3" while nobody's tagged yet, "1/3" while partially tagged, and back to just
    /// "3" (rendered in FaceCountBadgeBrush's dark green) once every detected face has a
    /// confirmed real-person label.</summary>
    public string FaceCountDisplay => TaggedFaceCount > 0 && TaggedFaceCount < DetectedFaceCount
        ? $"{TaggedFaceCount}/{DetectedFaceCount}"
        : DetectedFaceCount.ToString();

    public Brush FaceCountBadgeBrush =>
        DetectedFaceCount > 0 && TaggedFaceCount == DetectedFaceCount ? AllTaggedBadgeBrush : DefaultBadgeBrush;

    /// <summary>
    /// Lazily fetched from the db on first access (i.e. when the item is actually
    /// realized by the virtualizing row panel), so off-screen rows never hold a decoded
    /// bitmap or query the thumbnail blob at all.
    /// </summary>
    public BitmapImage? Thumbnail
    {
        get
        {
            if (!_thumbnailLoadAttempted && Model.HasThumbnail)
            {
                _thumbnailLoadAttempted = true;
                byte[]? bytes = _repo.GetThumbnail(Model.Id);
                if (bytes is not null)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = new MemoryStream(bytes);
                    bmp.EndInit();
                    bmp.Freeze();
                    _thumbnail = bmp;
                }
            }
            return _thumbnail;
        }
    }

    public void RefreshStatus()
    {
        OnPropertyChanged(nameof(RemoteStatus));
        OnPropertyChanged(nameof(RemoteUrl));
        OnPropertyChanged(nameof(UploadCropMode));
        OnPropertyChanged(nameof(UploadCropModeShort));
        OnPropertyChanged(nameof(CropOffsetX));
        OnPropertyChanged(nameof(CropOffsetY));
        OnPropertyChanged(nameof(CropRatioOverride));
    }

    public void NotifyRatingChanged() => OnPropertyChanged(nameof(Rating));

    public void NotifyAvatarTypeChanged() => OnPropertyChanged(nameof(AvatarType));

    public void NotifyMetadataChanged()
    {
        OnPropertyChanged(nameof(HasVrcxMetadata));
        OnPropertyChanged(nameof(PlayersTooltip));
        OnPropertyChanged(nameof(AuthorDisplayName));
        OnPropertyChanged(nameof(WorldName));
    }

    public void NotifyThumbnailReady()
    {
        _thumbnail = null;
        _thumbnailLoadAttempted = false;
        OnPropertyChanged(nameof(Thumbnail));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

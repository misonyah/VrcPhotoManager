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

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using VrcdnManager.Data;
using VrcdnManager.Models;

namespace VrcdnManager.ViewModels;

public class PhotoViewModel : INotifyPropertyChanged
{
    public Photo Model { get; }
    private readonly PhotoRepository _repo;

    private BitmapImage? _thumbnail;
    private bool _thumbnailLoadAttempted;

    public PhotoViewModel(Photo model, PhotoRepository repo)
    {
        Model = model;
        _repo = repo;
    }

    public string FileName => Model.FileName;
    public string? Rating => Model.Rating;
    public RemoteStatus RemoteStatus => Model.RemoteStatus;
    public string? RemoteUrl => Model.RemoteUrl;
    public string? AuthorDisplayName => Model.AuthorDisplayName;
    public string? WorldName => Model.WorldName;
    public string? PlayerNames => Model.PlayerNames;

    public string PlayersTooltip => Model.MetadataScanned
        ? (Model.PlayerNames is null ? "No VRCX metadata" : $"{Model.WorldName}\nPlayers: {Model.PlayerNames}")
        : "Not scanned yet";

    public bool Selected
    {
        get => Model.Selected;
        set
        {
            if (Model.Selected == value) return;
            Model.Selected = value;
            OnPropertyChanged();
        }
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
        }
    }

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

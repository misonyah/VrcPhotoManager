using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
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
    public RemoteStatus RemoteStatus => Model.RemoteStatus;
    public string? RemoteUrl => Model.RemoteUrl;
    public string? AuthorDisplayName => Model.AuthorDisplayName;
    public string? WorldName => Model.WorldName;
    public string? PlayerNames => Model.PlayerNames;

    public string PlayersTooltip => Model.MetadataScanned
        ? (Model.PlayerNames is null ? "No VRCX metadata" : $"{Model.WorldName}\nPlayers: {Model.PlayerNames}")
        : "Not scanned yet";

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

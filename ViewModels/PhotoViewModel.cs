using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using VrcdnManager.Models;

namespace VrcdnManager.ViewModels;

public class PhotoViewModel : INotifyPropertyChanged
{
    public Photo Model { get; }

    private BitmapImage? _thumbnail;
    private bool _thumbnailLoadAttempted;

    public PhotoViewModel(Photo model)
    {
        Model = model;
    }

    public string FileName => Model.FileName;
    public string? Rating => Model.Rating;
    public RemoteStatus RemoteStatus => Model.RemoteStatus;
    public string? RemoteUrl => Model.RemoteUrl;

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

    /// <summary>
    /// Lazily decoded on first access (i.e. when the item is actually realized by the
    /// virtualizing row panel), so off-screen rows never hold a decoded bitmap.
    /// </summary>
    public BitmapImage? Thumbnail
    {
        get
        {
            if (!_thumbnailLoadAttempted && Model.ThumbnailPath is not null && File.Exists(Model.ThumbnailPath))
            {
                _thumbnailLoadAttempted = true;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(Model.ThumbnailPath);
                bmp.EndInit();
                bmp.Freeze();
                _thumbnail = bmp;
            }
            return _thumbnail;
        }
    }

    public void RefreshStatus()
    {
        OnPropertyChanged(nameof(RemoteStatus));
        OnPropertyChanged(nameof(RemoteUrl));
    }

    public void ClearThumbnailCache()
    {
        _thumbnail = null;
        _thumbnailLoadAttempted = false;
        OnPropertyChanged(nameof(Thumbnail));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

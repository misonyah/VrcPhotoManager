using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Data.Sqlite;
using VrcdnManager.Data;
using VrcdnManager.Models;
using VrcdnManager.Services;
using VrcdnManager.Views;

namespace VrcdnManager.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];
    private const string VrcdnUsername = "misonyah";
    private const double RowMargin = 8;

    private readonly PhotoRepository _repo;
    private readonly ThumbnailService _thumbnails;
    private readonly CredentialStore _credentials;
    private VrcdnApiClient? _api;

    private readonly List<PhotoViewModel> _allPhotos = [];
    public ObservableCollection<PhotoRow> Rows { get; } = [];

    private double _thumbnailSize = 160;
    public double ThumbnailSize
    {
        get => _thumbnailSize;
        set
        {
            if (Math.Abs(_thumbnailSize - value) < 0.5) return;
            _thumbnailSize = value;
            OnPropertyChanged();
            RebuildRows();
        }
    }

    private double _gridWidth = 1000;
    public double GridWidth
    {
        get => _gridWidth;
        set
        {
            if (Math.Abs(_gridWidth - value) < 1) return;
            _gridWidth = value;
            RebuildRows();
        }
    }

    private string _ratingFilter = "All";
    public string RatingFilter
    {
        get => _ratingFilter;
        set { _ratingFilter = value; OnPropertyChanged(); RebuildRows(); }
    }
    public string[] RatingFilterOptions { get; } = ["All", "general", "sensitive", "questionable", "explicit", "(none)"];

    private string _statusFilter = "All";
    public string StatusFilter
    {
        get => _statusFilter;
        set { _statusFilter = value; OnPropertyChanged(); RebuildRows(); }
    }
    public string[] StatusFilterOptions { get; } = ["All", "NotUploaded", "Uploading", "Uploaded", "Failed"];

    private string _statusMessage = "Not logged in. Click Login to start.";
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public ICommand ScanLibraryCommand { get; }
    public ICommand ImportRatingsCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand SyncMetadataCommand { get; }
    public ICommand UploadSelectedCommand { get; }

    public MainViewModel()
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VrcdnManager");
        Directory.CreateDirectory(dataDir);

        _repo = new PhotoRepository(Path.Combine(dataDir, "vrcdn_manager.db"));
        _thumbnails = new ThumbnailService();
        _credentials = new CredentialStore(_repo);

        foreach (var photo in _repo.GetAll())
        {
            _allPhotos.Add(new PhotoViewModel(photo, _repo));
        }
        RebuildRows();

        ScanLibraryCommand = new RelayCommand(ScanLibraryAsync);
        ImportRatingsCommand = new RelayCommand(ImportRatingsAsync);
        LoginCommand = new RelayCommand(LoginAsync);
        SyncMetadataCommand = new RelayCommand(SyncMetadataAsync);
        UploadSelectedCommand = new RelayCommand(UploadSelectedAsync);

        TryAutoLogin();
    }

    private void TryAutoLogin()
    {
        try
        {
            string? cookie = _credentials.LoadCookie(null);
            if (cookie is not null)
            {
                _api = new VrcdnApiClient(cookie);
                StatusMessage = "Logged in (restored session).";
            }
        }
        catch (InvalidOperationException)
        {
            StatusMessage = "Saved session is password-protected. Click Login to unlock.";
        }
    }

    private async Task LoginAsync()
    {
        var window = new LoginWindow();
        bool? result = window.ShowDialog();
        if (result != true || window.SessionCookie is null)
        {
            StatusMessage = "Login cancelled.";
            return;
        }

        _credentials.SaveCookie(window.SessionCookie, null);
        _api = new VrcdnApiClient(window.SessionCookie);
        StatusMessage = "Logged in.";
        await Task.CompletedTask;
    }

    private async Task ScanLibraryAsync()
    {
        string root = @"C:\Users\redacted\Pictures\VRChat";
        StatusMessage = "Scanning library...";
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        int processed = 0;
        foreach (var chunk in Chunk(files, 25))
        {
            foreach (var path in chunk)
            {
                var info = new FileInfo(path);
                long id = _repo.UpsertLocalFile(path, info.Length, info.LastWriteTimeUtc.ToOADate());

                var existing = _allPhotos.FirstOrDefault(p => p.Model.LocalPath == path);
                if (existing is null)
                {
                    var model = new Photo { Id = id, LocalPath = path, FileSize = info.Length, Mtime = info.LastWriteTimeUtc.ToOADate() };
                    existing = new PhotoViewModel(model, _repo);
                    _allPhotos.Add(existing);
                }

                if (!existing.Model.HasThumbnail)
                {
                    try
                    {
                        byte[] thumbnail = await _thumbnails.GenerateThumbnailAsync(path);
                        _repo.SetThumbnail(id, thumbnail);
                        existing.Model.HasThumbnail = true;
                        existing.NotifyThumbnailReady();
                    }
                    catch (Exception ex)
                    {
                        StatusMessage = $"Thumbnail failed for {Path.GetFileName(path)}: {ex.Message}";
                    }
                }
            }
            processed += chunk.Count;
            StatusMessage = $"Scanning... {processed}/{files.Count}";
            RebuildRows();
        }

        StatusMessage = $"Scan complete: {files.Count} photos.";
    }

    private async Task ImportRatingsAsync()
    {
        const string indexDbPath = @"D:\AI-Tools\wd14-tagger\index.db";
        if (!File.Exists(indexDbPath))
        {
            StatusMessage = "WD14 index.db not found at D:\\AI-Tools\\wd14-tagger\\index.db";
            return;
        }

        StatusMessage = "Importing ratings from WD14 index...";
        using var conn = new SqliteConnection($"Data Source={indexDbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, rating FROM photos";
        using var reader = cmd.ExecuteReader();

        int updated = 0;
        while (reader.Read())
        {
            string path = reader.GetString(0);
            string rating = reader.GetString(1);
            string fileName = Path.GetFileName(path);
            _repo.SetRatingByFileName(fileName, rating);

            var match = _allPhotos.FirstOrDefault(p => p.FileName == fileName);
            if (match is not null)
            {
                match.Model.Rating = rating;
                updated++;
            }
        }

        StatusMessage = $"Imported ratings for {updated} photos.";
        RebuildRows();
    }

    private async Task SyncMetadataAsync()
    {
        if (_api is null) { StatusMessage = "Log in first."; return; }

        StatusMessage = "Syncing metadata from VRCDN...";
        var remoteObjects = await _api.ListObjectsAsync();
        var unresolved = _repo.SyncRemoteMatches(
            remoteObjects.Select(o => (o.Original, o.Id, o.Extension, o.Size)),
            VrcdnUsername);

        // refresh in-memory view models from the db
        var refreshed = _repo.GetAll().ToDictionary(p => p.LocalPath);
        foreach (var vm in _allPhotos)
        {
            if (refreshed.TryGetValue(vm.Model.LocalPath, out var updated))
            {
                vm.Model.RemoteStatus = updated.RemoteStatus;
                vm.Model.RemoteUrl = updated.RemoteUrl;
                vm.Model.RemoteId = updated.RemoteId;
                vm.RefreshStatus();
            }
        }

        StatusMessage = unresolved.Count == 0
            ? $"Sync complete: {remoteObjects.Count} remote objects matched."
            : $"Sync complete: {remoteObjects.Count - unresolved.Count} matched, {unresolved.Count} remote objects had no local match.";
    }

    private async Task UploadSelectedAsync()
    {
        if (_api is null) { StatusMessage = "Log in first."; return; }

        var toUpload = _allPhotos.Where(p => p.Selected && p.RemoteStatus != RemoteStatus.Uploaded).ToList();
        if (toUpload.Count == 0) { StatusMessage = "Nothing selected to upload."; return; }

        int done = 0;
        foreach (var vm in toUpload)
        {
            vm.Model.RemoteStatus = RemoteStatus.Uploading;
            vm.RefreshStatus();
            _repo.UpdateRemoteStatus(vm.Model.Id, RemoteStatus.Uploading);

            try
            {
                byte[] resized = await _thumbnails.PrepareForUploadAsync(vm.Model.LocalPath);
                string uploadFileName = Path.GetFileNameWithoutExtension(vm.FileName) + ".jpg";
                await _api.UploadBytesAsync(uploadFileName, resized);
                vm.Model.RemoteStatus = RemoteStatus.Uploaded;
                vm.Model.UploadedAt = DateTime.UtcNow.ToString("o");
                _repo.UpdateRemoteStatus(vm.Model.Id, RemoteStatus.Uploaded, uploadedAt: vm.Model.UploadedAt);
            }
            catch (Exception ex)
            {
                vm.Model.RemoteStatus = RemoteStatus.Failed;
                _repo.UpdateRemoteStatus(vm.Model.Id, RemoteStatus.Failed);
                StatusMessage = $"Failed to upload {vm.FileName}: {ex.Message}";
            }
            vm.RefreshStatus();

            done++;
            StatusMessage = $"Uploading... {done}/{toUpload.Count}";
            await Task.Delay(300);
        }

        StatusMessage = $"Upload complete: {done}/{toUpload.Count} processed.";
        await SyncMetadataAsync();
    }

    private void RebuildRows()
    {
        IEnumerable<PhotoViewModel> filtered = _allPhotos;
        if (RatingFilter != "All")
        {
            filtered = RatingFilter == "(none)"
                ? filtered.Where(p => p.Rating is null)
                : filtered.Where(p => p.Rating == RatingFilter);
        }
        if (StatusFilter != "All")
        {
            filtered = filtered.Where(p => p.RemoteStatus.ToString() == StatusFilter);
        }

        int columns = Math.Max(1, (int)(_gridWidth / (_thumbnailSize + RowMargin)));

        Application.Current.Dispatcher.Invoke(() =>
        {
            Rows.Clear();
            foreach (var chunk in Chunk(filtered.ToList(), columns))
            {
                Rows.Add(new PhotoRow(chunk));
            }
        });
    }

    private static List<List<T>> Chunk<T>(List<T> source, int size)
    {
        var result = new List<List<T>>();
        for (int i = 0; i < source.Count; i += size)
        {
            result.Add(source.GetRange(i, Math.Min(size, source.Count - i)));
        }
        return result;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using VrcPhotoManager.Data;
using VrcPhotoManager.Models;
using VrcPhotoManager.Services;
using VrcPhotoManager.Views;

namespace VrcPhotoManager.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];
    private const double RowMargin = 8;

    private readonly PhotoRepository _repo;
    private readonly ThumbnailService _thumbnails;
    private readonly CredentialStore _credentials;
    private readonly FaceRepository _faces;
    private FaceDetectionService? _faceDetector;
    private VrcxProfileLookupService? _profileLookup;
    private ClipEmbeddingService? _clipEmbedder;
    private WdTaggerService? _tagger;
    private VrcdnApiClient? _api;

    /// <summary>Cancelled when the window closes, so a long-running background scan stops
    /// starting new file work instead of racing the process teardown.</summary>
    private readonly CancellationTokenSource _shutdownCts = new();

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

    private string _sortOption = "Filename (A-Z)";
    public string SortOption
    {
        get => _sortOption;
        set { _sortOption = value; OnPropertyChanged(); RebuildRows(); }
    }
    public string[] SortOptions { get; } = ["Filename (A-Z)", "Date (Newest First)", "Date (Oldest First)"];

    public record PlayerFilterOption(string? VrcUserId, string DisplayText);

    private static readonly PlayerFilterOption AllPlayersOption = new(null, "(all players)");

    private PlayerFilterOption _selectedPlayerFilter = AllPlayersOption;
    public PlayerFilterOption SelectedPlayerFilter
    {
        get => _selectedPlayerFilter;
        set
        {
            _selectedPlayerFilter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanFilterTaggedOnly));
            RebuildRows();
        }
    }

    private List<PlayerFilterOption> _playerFilterOptions = [AllPlayersOption];
    public List<PlayerFilterOption> PlayerFilterOptions
    {
        get => _playerFilterOptions;
        private set { _playerFilterOptions = value; OnPropertyChanged(); }
    }

    /// <summary>The "Tagged only" checkbox is meaningless with no specific player selected.</summary>
    public bool CanFilterTaggedOnly => SelectedPlayerFilter.VrcUserId is not null;

    private bool _taggedOnlyFilter;
    public bool TaggedOnlyFilter
    {
        get => _taggedOnlyFilter;
        set { _taggedOnlyFilter = value; OnPropertyChanged(); RebuildRows(); }
    }

    private string _statusMessage = "Not logged in. Click Login to start.";
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public ICommand ScanLibraryCommand { get; }
    public RelayCommand ScanFacesCommand { get; }
    public RelayCommand SuggestFacesCommand { get; }
    public RelayCommand ClassifyPhotosCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand SyncMetadataCommand { get; }
    public RelayCommand UploadSelectedCommand { get; }
    public RelayCommand RemoveFromVrcdnCommand { get; }
    public ICommand CropPrintSelectedCommand { get; }

    /// <summary>Exposed so the Settings window (opened from code-behind, like AboutWindow/
    /// MetadataWindow) can read/write the WD14 model path settings.</summary>
    public PhotoRepository Repo => _repo;

    /// <summary>Exposed so MainWindow's code-behind can open TagFacesWindow (opened from
    /// code-behind, like MetadataWindow/SettingsWindow).</summary>
    public FaceRepository Faces => _faces;
    public VrcxProfileLookupService? ProfileLookup => _profileLookup;

    public MainViewModel()
    {
        // Deliberately still "VrcdnManager" - the on-disk data folder name, kept stable
        // across the app's rename so existing installs don't lose their database.
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VrcdnManager");
        Directory.CreateDirectory(dataDir);

        // Keep the constructor cheap - it runs on the UI thread before the window can even
        // paint itself. Loading the ONNX model (a few seconds) and querying thousands of
        // photos here made the whole window appear white/unresponsive until construction
        // finished. Both are deferred to InitializeAsync, run after the window is visible.
        _repo = new PhotoRepository(Path.Combine(dataDir, "vrcdn_manager.db"));
        _thumbnails = new ThumbnailService();
        _credentials = new CredentialStore(_repo);
        _faces = new FaceRepository(Path.Combine(dataDir, "vrcdn_manager.db"));

        ScanLibraryCommand = new RelayCommand(ScanLibraryAsync);
        ScanFacesCommand = new RelayCommand(ScanFacesAsync, () => _faceDetector is not null);
        SuggestFacesCommand = new RelayCommand(SuggestFacesAsync, () => _clipEmbedder is not null);
        ClassifyPhotosCommand = new RelayCommand(ClassifyPhotosAsync, () => _tagger is not null);
        LoginCommand = new RelayCommand(LoginAsync);
        SyncMetadataCommand = new RelayCommand(SyncMetadataAsync);
        UploadSelectedCommand = new RelayCommand(UploadSelectedAsync, CanUploadSelected);
        RemoveFromVrcdnCommand = new RelayCommand(RemoveFromVrcdnAsync, CanRemoveFromVrcdn);
        CropPrintSelectedCommand = new RelayCommand(CropPrintSelectedAsync);

        _statusMessage = "Loading...";
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        var photos = await Task.Run(() => _repo.GetAll());
        foreach (var photo in photos)
        {
            AddPhoto(new PhotoViewModel(photo, _repo));
        }
        RebuildRows();
        StatusMessage = $"{_allPhotos.Count} photos loaded.";
        ApplyFaceCounts();
        RefreshPlayerFilterOptions();

        TryAutoLogin();

        var (tagger, taggerError) = await Task.Run(() =>
        {
            var t = WdTaggerService.TryCreate(ResolveWdTaggerModelDir(), out string? error);
            return (t, error);
        });
        _tagger = tagger;
        if (_tagger is null)
        {
            StatusMessage = $"WD14 classifier unavailable: {taggerError}";
        }
        ClassifyPhotosCommand.RaiseCanExecuteChanged();

        var (faceDetector, faceDetectorError) = await Task.Run(() =>
        {
            var d = FaceDetectionService.TryCreate(out string? error);
            return (d, error);
        });
        _faceDetector = faceDetector;
        if (_faceDetector is null)
        {
            StatusMessage = $"Face detector unavailable: {faceDetectorError}";
        }
        ScanFacesCommand.RaiseCanExecuteChanged();

        var (profileLookup, profileLookupError) = await Task.Run(() =>
        {
            var s = VrcxProfileLookupService.TryCreate(out string? error);
            return (s, error);
        });
        _profileLookup = profileLookup;
        if (_profileLookup is null)
        {
            StatusMessage = $"VRCX profile-picture bootstrap unavailable: {profileLookupError}";
        }

        var (clipEmbedder, clipError) = await Task.Run(() =>
        {
            string? modelDir = _repo.GetStringSetting(SettingsKeys.ClipModelDir);
            if (modelDir is null) return (null, "CLIP model directory not configured (set it via Settings).");
            var s = ClipEmbeddingService.TryCreate(modelDir, out string? error);
            return (s, error);
        });
        _clipEmbedder = clipEmbedder;
        SuggestFacesCommand.RaiseCanExecuteChanged();
        if (_clipEmbedder is null)
        {
            StatusMessage = $"Face-matching unavailable: {clipError}";
        }
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
                RaiseSelectionDependentCommands();
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
        RaiseSelectionDependentCommands();
        await Task.CompletedTask;
    }

    /// <summary>Registers a photo with the library and wires its selection changes through
    /// to the Upload/Remove commands' CanExecute, so those buttons stay disabled until
    /// there's actually something selected they'd act on.</summary>
    private void AddPhoto(PhotoViewModel vm)
    {
        vm.SelectionChanged += (_, _) => RaiseSelectionDependentCommands();
        _allPhotos.Add(vm);
    }

    private bool CanUploadSelected() =>
        _api is not null && _allPhotos.Any(p => p.Selected && p.RemoteStatus != RemoteStatus.Uploaded);

    private bool CanRemoveFromVrcdn() =>
        _api is not null && _allPhotos.Any(p => p.Selected && p.RemoteStatus == RemoteStatus.Uploaded);

    /// <summary>Called from MainWindow's Closing handler - lets an in-progress Scan Library
    /// stop starting new file work promptly instead of continuing to churn as the app exits.</summary>
    public void RequestShutdown() => _shutdownCts.Cancel();

    private void RaiseSelectionDependentCommands()
    {
        UploadSelectedCommand.RaiseCanExecuteChanged();
        RemoveFromVrcdnCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Result of the background-thread probe for one file - kept free of any
    /// PhotoViewModel/Model references, since those are only ever touched back on the UI
    /// thread once this returns.</summary>
    private record ScanProbeResult(VrcxPhotoMetadata? Metadata, int? Width, int? Height);

    private async Task ScanLibraryAsync()
    {
        var token = _shutdownCts.Token;

        // VRChat's own default screenshot location, regardless of which account runs this.
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VRChat");
        StatusMessage = "Scanning library...";

        // Directory enumeration itself is synchronous I/O - offload it too so a large tree
        // doesn't cause even a brief startup hitch.
        List<string> files;
        try
        {
            files = await Task.Run(() => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList(), token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
            return;
        }

        int processed = 0;
        foreach (var chunk in Chunk(files, 25))
        {
            if (token.IsCancellationRequested) { StatusMessage = "Scan cancelled."; return; }

            foreach (var path in chunk)
            {
                var info = new FileInfo(path);
                long id = _repo.UpsertLocalFile(path, info.Length, info.LastWriteTimeUtc.ToOADate());

                var existing = _allPhotos.FirstOrDefault(p => p.Model.LocalPath == path);
                if (existing is null)
                {
                    var model = new Photo { Id = id, LocalPath = path, FileSize = info.Length, Mtime = info.LastWriteTimeUtc.ToOADate() };
                    existing = new PhotoViewModel(model, _repo);
                    AddPhoto(existing);
                }

                // Re-checks photos that previously found real metadata but predate the
                // AuthorId/PhotoPlayer columns (a one-time backfill) - skips photos already
                // confirmed to have none, since re-parsing those files would be pure waste.
                bool needsMetadataScan = !existing.Model.MetadataScanned
                    || (existing.Model.AuthorDisplayName is not null && existing.Model.AuthorId is null);
                bool needsDimensions = existing.Model.Width is null;

                if (needsMetadataScan || needsDimensions)
                {
                    ScanProbeResult probe;
                    try
                    {
                        // The actual file I/O and PNG/bitmap parsing runs off the UI thread -
                        // this is what previously made Scan Library freeze the window, since
                        // only thumbnail generation was ever backgrounded.
                        probe = await Task.Run(() =>
                        {
                            var meta = needsMetadataScan ? PngMetadataReader.TryReadVrcxMetadata(path) : null;
                            int? w = null, h = null;
                            if (needsDimensions)
                            {
                                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                                    new Uri(path), System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                                    System.Windows.Media.Imaging.BitmapCacheOption.None);
                                w = decoder.Frames[0].PixelWidth;
                                h = decoder.Frames[0].PixelHeight;
                            }
                            return new ScanProbeResult(meta, w, h);
                        }, token);
                    }
                    catch (OperationCanceledException)
                    {
                        StatusMessage = "Scan cancelled.";
                        return;
                    }
                    catch (Exception ex)
                    {
                        StatusMessage = $"Couldn't scan {Path.GetFileName(path)}: {ex.Message}";
                        probe = new ScanProbeResult(null, null, null);
                    }

                    if (needsMetadataScan)
                    {
                        var meta = probe.Metadata;
                        string? playerNames = meta?.Players is { Count: > 0 }
                            ? string.Join("\n", meta.Players.Select(p => $"{p.DisplayName} {{{p.Id}}}"))
                            : null;
                        var players = meta?.Players?.Select(p => (p.Id, p.DisplayName));
                        _repo.SetVrcxMetadata(id, meta?.Author?.Id, meta?.Author?.DisplayName, meta?.World?.Name, playerNames, players);
                        existing.Model.MetadataScanned = true;
                        existing.Model.AuthorId = meta?.Author?.Id;
                        existing.Model.AuthorDisplayName = meta?.Author?.DisplayName;
                        existing.Model.WorldName = meta?.World?.Name;
                        existing.Model.PlayerNames = playerNames;
                        existing.NotifyMetadataChanged();
                    }

                    if (needsDimensions && probe.Width is int w2 && probe.Height is int h2)
                    {
                        _repo.SetImageDimensions(id, w2, h2);
                        existing.Model.Width = w2;
                        existing.Model.Height = h2;
                    }
                }

                if (!existing.Model.HasThumbnail)
                {
                    try
                    {
                        byte[] thumbnail = await _thumbnails.GenerateThumbnailAsync(path, token);
                        _repo.SetThumbnail(id, thumbnail);
                        existing.Model.HasThumbnail = true;
                        existing.NotifyThumbnailReady();
                    }
                    catch (OperationCanceledException)
                    {
                        StatusMessage = "Scan cancelled.";
                        return;
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

    private async Task ScanFacesAsync()
    {
        if (_faceDetector is null)
        {
            StatusMessage = "Face detector not available.";
            return;
        }

        StatusMessage = "Scanning for faces...";
        var photos = _allPhotos.ToList();
        int processed = 0, totalFaces = 0;
        foreach (var vm in photos)
        {
            try
            {
                var faces = await Task.Run(() => _faceDetector.DetectFaces(vm.Model.LocalPath));
                _faces.InsertDetectedFaces(vm.Model.Id, faces);
                totalFaces += faces.Count;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Face detection failed for {vm.FileName}: {ex.Message}";
            }

            processed++;
            if (processed % 25 == 0 || processed == photos.Count)
            {
                StatusMessage = $"Scanning for faces... {processed}/{photos.Count} photos, {totalFaces} faces found so far";
            }
        }

        ApplyFaceCounts();
        StatusMessage = $"Face scan complete: {totalFaces} faces found across {photos.Count} photos.";
    }

    private async Task SuggestFacesAsync()
    {
        if (_clipEmbedder is null) { StatusMessage = "CLIP face-matching model not available."; return; }

        StatusMessage = "Computing face embeddings...";
        var pathById = _allPhotos.ToDictionary(p => p.Model.Id, p => p.Model.LocalPath);
        var needingEmbedding = _faces.GetDetectedFacesWithoutEmbedding();
        int embedded = 0;
        foreach (var face in needingEmbedding)
        {
            if (!pathById.TryGetValue(face.PhotoId, out string? path)) continue;
            try
            {
                float[] embedding = await Task.Run(() =>
                    _clipEmbedder.ComputeEmbedding(path, face.X, face.Y, face.Width, face.Height));
                _faces.SetEmbedding(face.Id, ClipEmbeddingService.EmbeddingToBytes(embedding));
            }
            catch (Exception ex)
            {
                StatusMessage = $"Embedding failed for face {face.Id}: {ex.Message}";
            }

            embedded++;
            if (embedded % 25 == 0 || embedded == needingEmbedding.Count)
            {
                StatusMessage = $"Computing face embeddings... {embedded}/{needingEmbedding.Count}";
            }
        }

        StatusMessage = "Building reference centroids...";
        var persons = _faces.GetAllPersons();
        var centroids = new Dictionary<long, float[]>();
        foreach (var person in persons)
        {
            var refs = _faces.GetReferenceEmbeddingsForPerson(person.Id)
                .Select(ClipEmbeddingService.BytesToEmbedding).ToList();
            if (person.VrcProfileThumbnail is byte[] thumb)
            {
                try { refs.Add(await Task.Run(() => _clipEmbedder.ComputeEmbeddingFromBytes(thumb))); }
                catch { /* corrupt/unreadable thumbnail - skip it, may still have enough tag-derived refs */ }
            }

            var centroid = FaceMatcher.TryComputeCentroid(refs);
            if (centroid is not null) centroids[person.Id] = centroid;
        }

        if (centroids.Count == 0)
        {
            StatusMessage = $"No registered person has enough reference photos yet (need >= {FaceMatcher.MinReferenceEmbeddings}: profile picture + confirmed tags combined).";
            return;
        }

        StatusMessage = "Matching faces against registered people...";
        var toScore = _faces.GetFacesNeedingSuggestion();
        int suggested = 0;
        foreach (var face in toScore)
        {
            if (face.Embedding is null) continue;
            float[] faceEmbedding = ClipEmbeddingService.BytesToEmbedding(face.Embedding);

            var scored = centroids
                .Select(kv => (PersonId: kv.Key, Similarity: FaceMatcher.CosineSimilarity(faceEmbedding, kv.Value)))
                .OrderByDescending(s => s.Similarity)
                .ToList();

            var best = scored[0];
            bool accept;
            float confidence;
            if (scored.Count == 1)
            {
                accept = best.Similarity >= FaceMatcher.SingleCandidateThreshold;
                confidence = best.Similarity;
            }
            else
            {
                float margin = best.Similarity - scored[1].Similarity;
                accept = margin >= FaceMatcher.DifferentialMarginThreshold;
                confidence = margin;
            }

            if (accept)
            {
                _faces.UpsertFaceLabel(face.Id, best.PersonId, confirmed: false, FaceLabelSource.EmbeddingMatch, confidence);
                suggested++;
            }
        }

        StatusMessage = $"Suggest Faces done: {embedded} embeddings computed, {suggested} new suggestions across {centroids.Count} eligible people.";
    }

    /// <summary>Rebuilds the player-filter dropdown from the current library state - called
    /// once at startup and again after the Tag Faces window closes, so newly-tagged people
    /// show "(tagged)" without needing a full app restart.</summary>
    public void RefreshPlayerFilterOptions()
    {
        var taggedIds = _faces.GetTaggedUserIds();
        var options = new List<PlayerFilterOption> { AllPlayersOption };
        options.AddRange(_repo.GetDistinctPlayers().Select(p =>
            new PlayerFilterOption(p.UserId, taggedIds.Contains(p.UserId) ? $"{p.DisplayName} (tagged)" : p.DisplayName)));
        PlayerFilterOptions = options;
    }

    /// <summary>Pulls face counts in one bulk query and applies them to already-loaded
    /// PhotoViewModels - called after a scan, and once at startup so counts from a previous
    /// scan are visible immediately without re-scanning.</summary>
    private void ApplyFaceCounts()
    {
        var counts = _faces.GetFaceCountsByPhoto();
        foreach (var vm in _allPhotos)
        {
            vm.DetectedFaceCount = counts.GetValueOrDefault(vm.Model.Id, 0);
        }
    }

    /// <summary>
    /// Checks the path configured via the Settings window first, then a folder next to the
    /// exe (so a public build works for anyone who drops the model there), falling back to
    /// the dev machine's path this was built against.
    /// </summary>
    private string ResolveWdTaggerModelDir()
    {
        string? configured = _repo.GetStringSetting(SettingsKeys.WdModelDir);
        if (configured is not null && Directory.Exists(configured)) return configured;

        string local = Path.Combine(AppContext.BaseDirectory, "wd14-model");
        return Directory.Exists(local) ? local : @"D:\AI-Tools\wd14-tagger\model";
    }

    /// <summary>
    /// Runs the WD14 classifier in-process for any photo that still has no rating.
    /// </summary>
    private async Task ClassifyPhotosAsync()
    {
        if (_tagger is null) { StatusMessage = "WD14 classifier not available."; return; }

        var toClassify = _allPhotos.Where(p => p.Rating is null).ToList();
        if (toClassify.Count == 0) { StatusMessage = "Nothing to classify - every photo already has a rating."; return; }

        int done = 0;
        foreach (var vm in toClassify)
        {
            try
            {
                string rating = await Task.Run(() => _tagger.ClassifyRating(vm.Model.LocalPath));
                vm.Model.Rating = rating;
                _repo.SetRating(vm.Model.Id, rating);
                vm.NotifyRatingChanged();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Classification failed for {vm.FileName}: {ex.Message}";
            }

            done++;
            if (done % 20 == 0 || done == toClassify.Count)
            {
                StatusMessage = $"Classifying... {done}/{toClassify.Count}";
            }
        }

        StatusMessage = $"Classified {done} photos.";
        RebuildRows();
    }

    /// <summary>
    /// VRChat's "Print" feature pads photos to 2048x1440 with a white border around the real
    /// 1920x1080 content. Crops the border off selected photos that match, saving a new file
    /// (original untouched) and adding it to the library.
    /// </summary>
    private async Task CropPrintSelectedAsync()
    {
        var candidates = _allPhotos.Where(p => p.Selected && CropPrintService.LooksLikePrintFormat(p.Model.Width, p.Model.Height)).ToList();
        if (candidates.Count == 0) { StatusMessage = "No selected photos look like Print-format (2048x1440)."; return; }

        int cropped = 0, skipped = 0;
        foreach (var vm in candidates)
        {
            try
            {
                bool hasBorder = await Task.Run(() => CropPrintService.HasWhiteBorder(vm.Model.LocalPath));
                if (!hasBorder) { skipped++; continue; }

                string newPath = await Task.Run(() => CropPrintService.CropAndSave(vm.Model.LocalPath));
                var info = new FileInfo(newPath);
                long id = _repo.UpsertLocalFile(newPath, info.Length, info.LastWriteTimeUtc.ToOADate());
                _repo.SetImageDimensions(id, 1920, 1080);
                AddPhoto(new PhotoViewModel(new Photo { Id = id, LocalPath = newPath, FileSize = info.Length, Mtime = info.LastWriteTimeUtc.ToOADate(), Width = 1920, Height = 1080 }, _repo));
                cropped++;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Crop failed for {vm.FileName}: {ex.Message}";
            }
        }

        StatusMessage = $"Cropped {cropped} print-format photo(s), skipped {skipped} (no white border found).";
        RebuildRows();
    }

    private async Task SyncMetadataAsync()
    {
        if (_api is null) { StatusMessage = "Log in first."; return; }

        StatusMessage = "Syncing metadata from VRCDN...";
        string username;
        List<RemoteObject> remoteObjects;
        try
        {
            username = await _api.GetUsernameAsync();
            remoteObjects = await _api.ListObjectsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sync failed: {ex.Message}";
            return;
        }

        var unresolved = _repo.SyncRemoteMatches(
            remoteObjects.Select(o => (o.Original, o.Id, o.Extension, o.Size)),
            username);

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

                // Clear selection on success (so the button correctly disables once nothing
                // eligible remains, and the next batch starts from an empty selection) - but
                // leave failed uploads selected, so they're easy to spot and retry.
                vm.Selected = false;
                _repo.SetSelected(vm.Model.Id, false);
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
        RaiseSelectionDependentCommands();
        await SyncMetadataAsync();
    }

    /// <summary>
    /// Deletes selected, currently-uploaded photos from VRCDN's storage (not just local
    /// bookkeeping) via the same removeObject call the web panel uses, then resets their
    /// local status back to NotUploaded. This is destructive on VRCDN's end - the file has
    /// to be re-uploaded to get a working URL again - so it's confirmed before running.
    /// </summary>
    private async Task RemoveFromVrcdnAsync()
    {
        if (_api is null) { StatusMessage = "Log in first."; return; }

        var toRemove = _allPhotos.Where(p => p.Selected && p.RemoteStatus == RemoteStatus.Uploaded).ToList();
        if (toRemove.Count == 0) { StatusMessage = "Nothing selected is currently uploaded."; return; }

        var confirm = MessageBox.Show(
            $"Remove {toRemove.Count} photo(s) from VRCDN? This deletes them from your VRCDN storage " +
            "(any URL/photo-frame using them will break) - you'd need to re-upload to restore them.",
            "Remove from VRCDN", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) { StatusMessage = "Remove cancelled."; return; }

        int done = 0;
        foreach (var vm in toRemove)
        {
            try
            {
                if (vm.Model.RemoteId is not null)
                {
                    await _api.RemoveObjectAsync(vm.Model.RemoteId);
                }
                _repo.ClearRemoteStatus(vm.Model.Id);
                vm.Model.RemoteStatus = RemoteStatus.NotUploaded;
                vm.Model.RemoteUrl = null;
                vm.Model.RemoteId = null;
                vm.Model.UploadedAt = null;
                vm.RefreshStatus();
                done++;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to remove {vm.FileName}: {ex.Message}";
            }
        }

        StatusMessage = $"Removed {done}/{toRemove.Count} photo(s) from VRCDN.";
        RaiseSelectionDependentCommands();
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
        if (SelectedPlayerFilter.VrcUserId is string userId)
        {
            var photoIds = _repo.GetPhotoIdsForUser(userId);
            filtered = filtered.Where(p => photoIds.Contains(p.Model.Id));
            if (TaggedOnlyFilter)
            {
                var taggedPhotoIds = _faces.GetTaggedPhotoIdsForUser(userId);
                filtered = filtered.Where(p => taggedPhotoIds.Contains(p.Model.Id));
            }
        }

        filtered = SortOption switch
        {
            "Date (Newest First)" => filtered.OrderByDescending(p => p.Model.Mtime),
            "Date (Oldest First)" => filtered.OrderBy(p => p.Model.Mtime),
            _ => filtered.OrderBy(p => p.Model.LocalPath),
        };

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

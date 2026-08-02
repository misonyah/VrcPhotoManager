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
    private AvatarTypeService? _avatarClassifier;
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

    private string _avatarTypeFilter = "Any";
    public string AvatarTypeFilter
    {
        get => _avatarTypeFilter;
        set { _avatarTypeFilter = value; OnPropertyChanged(); RebuildRows(); }
    }

    private List<string> _avatarTypeFilterOptions = ["Any", "Unclassified", "No confident match"];
    public List<string> AvatarTypeFilterOptions
    {
        get => _avatarTypeFilterOptions;
        private set { _avatarTypeFilterOptions = value; OnPropertyChanged(); }
    }

    /// <summary>Rebuilds the avatar-type filter dropdown from the current library state -
    /// called once at startup and again after Classify Avatars runs, so newly-classified
    /// avatar types show up without needing a full app restart (same pattern as
    /// RefreshPlayerFilterOptions).</summary>
    public void RefreshAvatarTypeFilterOptions()
    {
        var options = new List<string> { "Any", "Unclassified", "No confident match" };
        options.AddRange(_repo.GetDistinctAvatarTypes());
        AvatarTypeFilterOptions = options;
    }

    private string _statusFilter = "All";
    public string StatusFilter
    {
        get => _statusFilter;
        set { _statusFilter = value; OnPropertyChanged(); RebuildRows(); }
    }
    public string[] StatusFilterOptions { get; } = ["All", "NotUploaded", "Uploading", "Uploaded", "Failed"];

    private string _faceCountFilter = "Any";
    public string FaceCountFilter
    {
        get => _faceCountFilter;
        set { _faceCountFilter = value; OnPropertyChanged(); RebuildRows(); }
    }
    public string[] FaceCountFilterOptions { get; } = ["Any", "0", "1+"];

    /// <summary>VRCX-recorded world-instance occupancy, not detected-face count. Defaults to
    /// "Any" (no filtering) - a numbered option like "1+ (per VRCX)" is a real filter, not a
    /// no-op: a photo VRCX never got metadata for (0 players recorded) would be excluded.
    /// "(per VRCX)" on every numbered option is a reminder this counts VRCX's recorded
    /// world-instance occupancy, not detected faces.</summary>
    private string _playerCountFilter = "Any";
    public string PlayerCountFilter
    {
        get => _playerCountFilter;
        set { _playerCountFilter = value; OnPropertyChanged(); RebuildRows(); }
    }
    public string[] PlayerCountFilterOptions { get; } =
        ["Any", "1+ (per VRCX)", "2+ (per VRCX)", "3+ (per VRCX)", "4+ (per VRCX)", "5+ (per VRCX)",
         "6+ (per VRCX)", "7+ (per VRCX)", "8+ (per VRCX)", "9+ (per VRCX)", "10+ (per VRCX)"];

    /// <summary>0 means "off" (no confidence-based filtering) - real confidence values are
    /// always > 0 in practice, since only suggestions clearing FaceMatcher's own acceptance
    /// threshold ever get written at all, so 0 as a sentinel doesn't collide with real data.</summary>
    private double _minSuggestionConfidence;
    public double MinSuggestionConfidence
    {
        get => _minSuggestionConfidence;
        set
        {
            _minSuggestionConfidence = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MinSuggestionConfidenceLabel));
            RebuildRows();
        }
    }
    public string MinSuggestionConfidenceLabel => _minSuggestionConfidence <= 0 ? "Off" : _minSuggestionConfidence.ToString("F2");

    private string _sortOption = "Filename (A-Z)";
    public string SortOption
    {
        get => _sortOption;
        set { _sortOption = value; OnPropertyChanged(); RebuildRows(); }
    }
    public string[] SortOptions { get; } =
    [
        "Filename (A-Z)", "Date (Newest First)", "Date (Oldest First)",
        "Untagged Faces (Most First)", "People in World (Most First)",
    ];

    /// <summary>
    /// A player filter entry is keyed by exactly one of VrcUserId (VRCX-observed player - the
    /// dropdown filters by VRCX's own "who was in this instance" data) or PersonId (a manually
    /// registered person with no VRC id - VRCX never observed them, so the only meaningful
    /// filter is "photos where this person has a confirmed face tag").
    /// </summary>
    public record PlayerFilterOption(string? VrcUserId, long? PersonId, string DisplayText)
    {
        public bool IsManual => PersonId is not null;
    }

    private static readonly PlayerFilterOption AllPlayersOption = new(null, null, "(all players)");

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
    public RelayCommand ClassifyAvatarsCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand SyncMetadataCommand { get; }
    public ICommand CrossReferenceGamelogCommand { get; }
    public ICommand SyncVrcPlayerDataCommand { get; }
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

    /// <summary>Read fresh (not cached) each time - it's a single cheap SQLite lookup, so
    /// there's no need for a separate "reload after Settings closes" step.</summary>
    public bool AutoCopyUrlOnHover => _repo.GetBoolSetting(SettingsKeys.AutoCopyVrcdnUrlOnHover);

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
        ClassifyAvatarsCommand = new RelayCommand(ClassifyAvatarsAsync, () => _avatarClassifier is not null);
        LoginCommand = new RelayCommand(LoginAsync);
        SyncMetadataCommand = new RelayCommand(SyncMetadataAsync);
        CrossReferenceGamelogCommand = new RelayCommand(CrossReferenceGamelogAsync);
        SyncVrcPlayerDataCommand = new RelayCommand(SyncVrcPlayerDataAsync);
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
        ApplyPlayerCounts();
        RefreshPlayerFilterOptions();
        RefreshAvatarTypeFilterOptions();

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

        string? avatarModelDir = ResolveAvatarModelDir();
        if (avatarModelDir is not null)
        {
            var (avatarClassifier, avatarError) = await Task.Run(() =>
            {
                var s = AvatarTypeService.TryCreate(avatarModelDir, out string? error);
                return (s, error);
            });
            _avatarClassifier = avatarClassifier;
            if (_avatarClassifier is null)
            {
                StatusMessage = $"Avatar classifier unavailable: {avatarError}";
            }
        }
        ClassifyAvatarsCommand.RaiseCanExecuteChanged();

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

        ApplyPlayerCounts();
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

    /// <summary>
    /// Fallback for photos with zero VRCX-embedded player data (e.g. taken by someone else in
    /// the same instance) - cross-references the local VRCX account's own gamelog instead. See
    /// GamelogCorrelationService and docs/superpowers/specs/2026-08-01-gamelog-player-inference-design.md.
    /// Deliberately opt-in via its own button rather than folded into Scan Library: it depends
    /// on this account's gamelog actually covering the photo's capture time, which won't always
    /// be true (VRCX closed, a gap in the log, a photo from before this account's gamelog
    /// starts), so it shouldn't run silently as part of the normal scan.
    /// </summary>
    private async Task CrossReferenceGamelogAsync()
    {
        using var gamelog = GamelogCorrelationService.TryCreate(out string? gamelogError);
        if (gamelog is null)
        {
            StatusMessage = $"Gamelog cross-reference unavailable: {gamelogError}";
            return;
        }

        var missingIds = _repo.GetPhotoIdsMissingPlayerData();
        var candidates = _allPhotos.Where(p => missingIds.Contains(p.Model.Id)).ToList();

        StatusMessage = "Cross-referencing gamelog...";
        int processed = 0, matched = 0;
        foreach (var vm in candidates)
        {
            if (GamelogCorrelationService.TryParseCaptureTime(vm.Model.LocalPath) is DateTime time)
            {
                var players = await Task.Run(() => gamelog.FindPresentPlayers(time));
                if (players is { Count: > 0 })
                {
                    _repo.InsertGamelogInferredPlayers(vm.Model.Id, players);
                    matched++;
                }
            }

            processed++;
            if (processed % 25 == 0 || processed == candidates.Count)
            {
                StatusMessage = $"Cross-referencing gamelog... {processed}/{candidates.Count} photos, {matched} matched so far";
            }
        }

        ApplyPlayerCounts();
        StatusMessage = $"Gamelog cross-reference done: {matched}/{candidates.Count} photos matched (of {candidates.Count} with no VRCX player data).";
    }

    /// <summary>
    /// Refreshes the permanent known-VRC-user cache (see KnownVrcUser) from VRCX's friends
    /// list and gamelog history, and captures any new name-history aliases (see VrcUserAlias) -
    /// both used to be done automatically every time Tag Faces opened, which a real slowness
    /// report traced to VRCX's gamelog table: it only grows, has no natural size bound, and
    /// this account's has reached thousands of distinct players, making that automatic refresh
    /// the single biggest chunk of a ~1s Tag Faces open time. Moved here as an explicit action
    /// instead - Tag Faces itself now just reads whatever this cache already has, so its
    /// freshness depends on running this periodically rather than being guaranteed current
    /// every time. Runs the DB/VRCX work off the UI thread, same shape as
    /// CrossReferenceGamelogAsync above.
    /// </summary>
    private async Task SyncVrcPlayerDataAsync()
    {
        if (_profileLookup is null)
        {
            StatusMessage = "VRC player sync unavailable - VRCX not found.";
            return;
        }

        StatusMessage = "Syncing VRC player data from VRCX...";
        int knownUserCount = await Task.Run(() =>
        {
            var friends = _profileLookup.GetFriends();
            var gamelogSeen = _profileLookup.GetGamelogSeenPlayers();
            if (_profileLookup.GetSelf() is (string selfId, string selfName))
            {
                friends.Insert(0, (selfId, selfName));
            }
            var knownUsers = _faces.UpsertKnownVrcUsersAndGetAll(friends.Concat(gamelogSeen));

            // Same alias auto-capture this used to do inline in TagFacesWindow's constructor -
            // filters out whatever's already the current/latest name for that user, so a
            // person's own current name never shows up as its own alias.
            var currentNames = friends.Concat(gamelogSeen).Concat(knownUsers)
                .GroupBy(p => p.UserId)
                .ToDictionary(g => g.Key, g => g.First().DisplayName);
            var historyCandidates = _profileLookup.GetFriendRenameHistory()
                .Concat(_profileLookup.GetGamelogNameHistory())
                .Where(c => !currentNames.TryGetValue(c.UserId, out var current)
                    || !string.Equals(current, c.Alias, StringComparison.Ordinal));
            _faces.CaptureAliasesFromHistory(historyCandidates);

            return knownUsers.Count;
        });

        StatusMessage = $"VRC player data synced: {knownUserCount} known players cached.";
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

        var vrcxPlayers = _repo.GetDistinctPlayers().Select(p =>
            (Name: p.DisplayName, Option: new PlayerFilterOption(p.UserId, null,
                taggedIds.Contains(p.UserId) ? $"{p.DisplayName} (tagged)" : p.DisplayName)));

        // Manually-created people (typed in the Tag Faces "new person" box, no linked VRC id)
        // never show up in VRCX's own player data - mixed into the same sorted list (not
        // appended after) so they land next to their alphabetical neighbors, marked "(manual)"
        // so they're visually distinct until linked to a real VRC account (see
        // PlayerFilterOption.IsManual / ItemContainerStyle in the XAML).
        var manualPeople = _faces.GetAllPersons()
            .Where(p => p.VrcUserId is null)
            .Select(p => (Name: p.Name, Option: new PlayerFilterOption(null, p.Id, $"{p.Name} (manual)")));

        var options = new List<PlayerFilterOption> { AllPlayersOption };
        options.AddRange(vrcxPlayers.Concat(manualPeople)
            .OrderBy(x => NaturalSortKey(x.Name), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Option));
        PlayerFilterOptions = options;
    }

    /// <summary>
    /// Powers the Player filter's autocomplete box (MainWindow code-behind) - same matching
    /// behavior as the Tag Faces person picker: FuzzyNameSearch tolerates VRCX's stylized
    /// Unicode display names, and a match via a recorded alias (see VrcUserAlias) finds
    /// someone under a name they no longer go by, not just their current one. An empty query
    /// returns the full option list, matching a plain dropdown's "click to browse everything".
    /// </summary>
    public List<PlayerFilterOption> SearchPlayerFilterOptions(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return PlayerFilterOptions;

        var aliasesByUserId = _faces.GetAllAliasesGroupedByUser();
        return PlayerFilterOptions.Where(o =>
            FuzzyNameSearch.Matches(o.DisplayText, query)
            || (o.VrcUserId is not null && aliasesByUserId.TryGetValue(o.VrcUserId, out var aliases)
                && aliases.Any(a => FuzzyNameSearch.Matches(a, query))))
            .ToList();
    }

    /// <summary>
    /// VRChat display names are full of decorative symbols, emoji, and stylized brackets
    /// (zero-width joiners, "★Aiko", "『Name』") - sorting on the raw string puts them in
    /// unicode-codepoint order, not where a human expects. Stripping everything but letters/
    /// digits before comparing sorts by what a person actually reads as the name; an
    /// all-symbol name (no letters/digits at all) falls back to the raw string so it still
    /// sorts somewhere stable instead of colliding with every other symbol-only name at "".
    /// </summary>
    private static string NaturalSortKey(string name)
    {
        var letters = name.Where(char.IsLetterOrDigit).ToArray();
        return letters.Length > 0 ? new string(letters) : name;
    }

    /// <summary>Pulls face counts in one bulk query and applies them to already-loaded
    /// PhotoViewModels - called after a scan, once at startup so counts from a previous scan
    /// are visible immediately without re-scanning, and after the Tag Faces window closes so
    /// the badge for the just-tagged photo updates without a full re-scan.</summary>
    public void ApplyFaceCounts()
    {
        var counts = _faces.GetFaceCountsByPhoto();
        foreach (var vm in _allPhotos)
        {
            var (total, tagged) = counts.GetValueOrDefault(vm.Model.Id, (0, 0));
            vm.DetectedFaceCount = total;
            vm.TaggedFaceCount = tagged;
        }
    }

    /// <summary>Pulls VRCX world-instance player counts in one bulk query - called after a
    /// library scan and once at startup, mirroring ApplyFaceCounts.</summary>
    private void ApplyPlayerCounts()
    {
        var counts = _repo.GetPlayerCountsByPhoto();
        foreach (var vm in _allPhotos)
        {
            vm.WorldPlayerCount = counts.GetValueOrDefault(vm.Model.Id, 0);
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

    private string? ResolveAvatarModelDir()
    {
        string? configured = _repo.GetStringSetting(SettingsKeys.AvatarModelDir);
        return configured is not null && Directory.Exists(configured) ? configured : null;
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

    /// <summary>Runs the avatar-type classifier in-process for any photo not yet
    /// classified (AvatarTypeConfidence is null) - re-running after a model update only
    /// touches those, same "already has a value, skip it" idiom as ClassifyPhotosAsync.</summary>
    private async Task ClassifyAvatarsAsync()
    {
        if (_avatarClassifier is null) { StatusMessage = "Avatar classifier not available."; return; }

        var missingIds = _repo.GetPhotoIdsMissingAvatarType();
        var toClassify = _allPhotos.Where(p => missingIds.Contains(p.Model.Id)).ToList();
        if (toClassify.Count == 0) { StatusMessage = "Nothing to classify - every photo already has an avatar-type result."; return; }

        int done = 0;
        foreach (var vm in toClassify)
        {
            try
            {
                var (label, confidence) = await Task.Run(() => _avatarClassifier.Classify(vm.Model.LocalPath));
                vm.Model.AvatarType = label;
                vm.Model.AvatarTypeConfidence = confidence;
                _repo.SetAvatarType(vm.Model.Id, label, confidence);
                vm.NotifyAvatarTypeChanged();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Avatar classification failed for {vm.FileName}: {ex.Message}";
            }

            done++;
            if (done % 20 == 0 || done == toClassify.Count)
            {
                StatusMessage = $"Classifying avatars... {done}/{toClassify.Count}";
            }
        }

        StatusMessage = $"Classified {done} photos' avatar types.";
        RefreshAvatarTypeFilterOptions();
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
        if (AvatarTypeFilter != "Any")
        {
            filtered = AvatarTypeFilter switch
            {
                "Unclassified" => filtered.Where(p => p.Model.AvatarTypeConfidence is null),
                "No confident match" => filtered.Where(p => p.Model.AvatarTypeConfidence is not null && p.AvatarType is null),
                _ => filtered.Where(p => p.AvatarType == AvatarTypeFilter),
            };
        }
        if (SelectedPlayerFilter.VrcUserId is string userId)
        {
            // "Tagged only" stands on its own - it must NOT further narrow the VRCX-presence
            // set below, since a confirmed face tag can exist on a photo VRCX never matched a
            // player to at all (e.g. a manually-drawn box, or metadata scanning missed them).
            // Requiring both would silently hide correctly-tagged photos (found via a real
            // report: Sayakiss tagged on a photo with zero photo_players rows).
            var photoIds = TaggedOnlyFilter
                ? _faces.GetTaggedPhotoIdsForUser(userId)
                : _repo.GetPhotoIdsForUser(userId);
            filtered = filtered.Where(p => photoIds.Contains(p.Model.Id));
        }
        else if (SelectedPlayerFilter.PersonId is long personId)
        {
            // Manual person - no VRCX presence data to filter from, so "selected" already
            // means "show their tagged photos" (see GetTaggedPhotoIdsForPerson).
            var taggedPhotoIds = _faces.GetTaggedPhotoIdsForPerson(personId);
            filtered = filtered.Where(p => taggedPhotoIds.Contains(p.Model.Id));
        }
        filtered = FaceCountFilter switch
        {
            "0" => filtered.Where(p => p.DetectedFaceCount == 0),
            "1+" => filtered.Where(p => p.DetectedFaceCount >= 1),
            _ => filtered,
        };
        if (PlayerCountFilter != "Any")
        {
            int minWorldPlayers = int.Parse(PlayerCountFilter.Split('+')[0]);
            filtered = filtered.Where(p => p.WorldPlayerCount >= minWorldPlayers);
        }
        if (MinSuggestionConfidence > 0)
        {
            var suggestedPhotoIds = _faces.GetPhotoIdsWithSuggestionConfidenceAtLeast((float)MinSuggestionConfidence);
            filtered = filtered.Where(p => suggestedPhotoIds.Contains(p.Model.Id));
        }

        filtered = SortOption switch
        {
            "Date (Newest First)" => filtered.OrderByDescending(p => p.Model.Mtime),
            "Date (Oldest First)" => filtered.OrderBy(p => p.Model.Mtime),
            // "Untagged" = detected but not yet confirmed - lets a person work through the
            // biggest tagging backlogs first instead of hunting for them in filename order.
            "Untagged Faces (Most First)" => filtered.OrderByDescending(p => p.DetectedFaceCount - p.TaggedFaceCount),
            "People in World (Most First)" => filtered.OrderByDescending(p => p.WorldPlayerCount),
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

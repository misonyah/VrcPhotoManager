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

    /// <summary>Public (not just used internally by RebuildRows' column-count math) so
    /// MainWindow.xaml.cs's cursor-anchored thumbnail-resize handler can compute the same row
    /// height this class uses, without a second hardcoded copy of the margin value.</summary>
    public const double RowMargin = 8;

    private readonly PhotoRepository _repo;
    private readonly ThumbnailService _thumbnails;
    private readonly CredentialStore _credentials;
    private readonly FaceRepository _faces;
    private readonly AvatarRegionRepository _avatarRegions;
    private FaceDetectionService? _faceDetector;
    private VrcxProfileLookupService? _profileLookup;
    private string? _selfUserId;
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
        // Coerce null back to "Any" - WPF's Selector can clear SelectedItem when the ComboBox's
        // ItemsSource is swapped for a new list instance (see RefreshAvatarTypeFilterOptions),
        // and that null propagates back through the TwoWay SelectedItem binding into this setter.
        // Without coercion, RebuildRows()'s switch falls into its "_ =>" arm and filters to
        // p.AvatarType == null instead of showing everything.
        set { _avatarTypeFilter = value ?? "Any"; OnPropertyChanged(); RebuildRows(); }
    }

    /// <summary>Value is what RebuildRows actually matches against p.AvatarType (and what
    /// AvatarTypeFilter itself holds) - kept separate from DisplayText so the dropdown can show
    /// a "(N)" classified-photo count without that count text becoming part of the match
    /// value (which would silently break the filter, since stored AvatarType text never
    /// includes a count suffix).</summary>
    public record AvatarTypeOption(string Value, string DisplayText);

    private List<AvatarTypeOption> _avatarTypeFilterOptions =
        [new("Any", "Any"), new("Unclassified", "Unclassified"), new("No confident match", "No confident match")];
    public List<AvatarTypeOption> AvatarTypeFilterOptions
    {
        get => _avatarTypeFilterOptions;
        private set { _avatarTypeFilterOptions = value; OnPropertyChanged(); }
    }

    /// <summary>Rebuilds the avatar-type filter dropdown from the current library state -
    /// called once at startup and again after Classify Avatars runs, so newly-classified
    /// avatar types (and updated counts) show up without needing a full app restart (same
    /// pattern as RefreshPlayerFilterOptions).</summary>
    public void RefreshAvatarTypeFilterOptions()
    {
        var counts = _repo.GetAvatarTypeCounts();
        var options = new List<AvatarTypeOption>
        {
            new("Any", "Any"),
            new("Unclassified", "Unclassified"),
            new("No confident match", "No confident match"),
        };
        options.AddRange(_repo.GetDistinctAvatarTypes()
            .Select(t => new AvatarTypeOption(t, $"{t} ({counts.GetValueOrDefault(t)})")));
        AvatarTypeFilterOptions = options;
    }

    private string _statusFilter = "All";
    public string StatusFilter
    {
        get => _statusFilter;
        set { _statusFilter = value; OnPropertyChanged(); RebuildRows(); }
    }
    public string[] StatusFilterOptions { get; } = ["All", "NotUploaded", "Uploading", "Uploaded", "Failed"];

    /// <summary>The literal value stored in Photo.UploadCropMode for an uploaded-but-uncropped
    /// photo is null - this is the filter-dropdown stand-in for that state, distinct from "Any"
    /// (no filtering at all). See GetFilteredSortedPhotos for how it's matched.</summary>
    public const string UploadCropModeOriginal = "Original (no crop)";

    private string _uploadCropModeFilter = "Any";
    public string UploadCropModeFilter
    {
        get => _uploadCropModeFilter;
        set { _uploadCropModeFilter = value; OnPropertyChanged(); RebuildRows(); }
    }

    private List<string> _uploadCropModeFilterOptions = ["Any", UploadCropModeOriginal];
    public List<string> UploadCropModeFilterOptions
    {
        get => _uploadCropModeFilterOptions;
        private set { _uploadCropModeFilterOptions = value; OnPropertyChanged(); }
    }

    /// <summary>Rebuilds the "Uploaded as" filter dropdown from the current library state -
    /// called once at startup and again after Upload Selected runs, same pattern as
    /// RefreshAvatarTypeFilterOptions.</summary>
    public void RefreshUploadCropModeFilterOptions()
    {
        var options = new List<string> { "Any", UploadCropModeOriginal };
        options.AddRange(_repo.GetDistinctUploadCropModes());
        UploadCropModeFilterOptions = options;
    }

    /// <summary>One entry in the Upload crop-mode dropdown (above the Upload Selected button) -
    /// AspectRatio is Width/Height, null for "don't crop, keep the original aspect ratio".
    /// IsCustom marks the one entry whose ratio instead comes from CustomCropRatioText at
    /// upload time, since an arbitrary user-typed ratio can't be a fixed preset value.</summary>
    public record UploadCropPreset(string Name, double? AspectRatio, bool IsCustom = false);

    public static readonly List<UploadCropPreset> UploadCropPresets =
    [
        new(UploadCropModeOriginal, null),
        new("1:1 (Square)", 1.0),
        new("3:4 (Portrait)", 3.0 / 4.0),
        new("4:3 (Landscape)", 4.0 / 3.0),
        new("9:16 (Portrait)", 9.0 / 16.0),
        new("16:9 (Landscape)", 16.0 / 9.0),
        new("Custom...", null, IsCustom: true),
    ];

    private UploadCropPreset _selectedUploadCropPreset = UploadCropPresets[0];
    public UploadCropPreset SelectedUploadCropPreset
    {
        get => _selectedUploadCropPreset;
        set
        {
            _selectedUploadCropPreset = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomCropRatioVisible));
            OnPropertyChanged(nameof(EffectiveCropAspectRatio));
        }
    }

    /// <summary>Drives the custom-ratio TextBox's Visibility next to the crop-preset dropdown -
    /// only meaningful (and only shown) when "Custom..." is the selected preset.</summary>
    public bool IsCustomCropRatioVisible => SelectedUploadCropPreset.IsCustom;

    private string _customCropRatioText = "5:7";
    public string CustomCropRatioText
    {
        get => _customCropRatioText;
        set
        {
            _customCropRatioText = value;
            OnPropertyChanged();
            if (SelectedUploadCropPreset.IsCustom) OnPropertyChanged(nameof(EffectiveCropAspectRatio));
        }
    }

    /// <summary>Width/Height the selected crop preset would apply, or null for "Original (no
    /// crop)" - drives the white crop-line preview overlay on selected thumbnails in the grid
    /// (see PhotoCellTemplate's crop-overlay Rectangle). Best-effort for Custom: an unparseable
    /// ratio just shows no preview rather than an error, since this is preview-only - the real
    /// validation/error happens in TryResolveUploadCropAspectRatio at actual upload time.</summary>
    public double? EffectiveCropAspectRatio =>
        SelectedUploadCropPreset.IsCustom ? TryParseCropRatio(CustomCropRatioText) : SelectedUploadCropPreset.AspectRatio;

    /// <summary>W:H (or WxH) - null if unparseable or non-positive, so callers can tell "typed
    /// something invalid" apart from a legitimate ratio.</summary>
    private static double? TryParseCropRatio(string text)
    {
        var parts = text.Split([':', 'x', 'X'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2
            && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out double w)
            && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out double h)
            && w > 0 && h > 0)
        {
            return w / h;
        }
        return null;
    }

    /// <summary>The actual crop ratio UploadSelectedAsync should apply - resolves the selected
    /// preset's fixed ratio, or parses CustomCropRatioText for the Custom preset. Null means
    /// "don't crop" UNLESS the Custom preset is selected with unparseable text, in which case
    /// the caller must treat this as an error, not as "no crop" - see
    /// TryResolveUploadCropAspectRatio.</summary>
    private bool TryResolveUploadCropAspectRatio(out double? aspectRatio, out string? error)
    {
        error = null;
        var preset = SelectedUploadCropPreset;
        if (!preset.IsCustom)
        {
            aspectRatio = preset.AspectRatio;
            return true;
        }

        aspectRatio = TryParseCropRatio(CustomCropRatioText);
        if (aspectRatio is null)
        {
            error = $"Invalid custom crop ratio \"{CustomCropRatioText}\" - use W:H, e.g. 5:7.";
            return false;
        }
        return true;
    }

    private string? ResolveUploadCropModeLabel(double? aspectRatio) =>
        aspectRatio is null ? null : SelectedUploadCropPreset.IsCustom ? $"Custom {CustomCropRatioText}" : SelectedUploadCropPreset.Name;

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

    /// <summary>Resets every filter (both the main bar's and FilterWindow's, since both bind to
    /// this same MainViewModel instance) back to "show everything" - each setter already raises
    /// its own OnPropertyChanged/RebuildRows, so this is just "set them all to their defaults"
    /// rather than needing its own separate notification/rebuild logic.</summary>
    public void ClearFilters()
    {
        RatingFilter = "All";
        StatusFilter = "All";
        UploadCropModeFilter = "Any";
        AvatarTypeFilter = "Any";
        SelectedPlayerFilter = AllPlayersOption;
        TaggedOnlyFilter = false;
        OwnPhotosOnlyFilter = false;
        FaceCountFilter = "Any";
        PlayerCountFilter = "Any";
        MinSuggestionConfidence = 0;
    }

    private string _sortOption = "Date (Newest First)";
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

    /// <summary>FilterWindow's multi-player filter list (see PlayerFilterRow) - always shaped
    /// with exactly one trailing empty row by EnsurePlayerFilterCriteriaShape, so picking a
    /// player in what was the last row automatically reveals a fresh empty one beneath it.
    /// GetFilteredSortedPhotos intersects every non-empty non-Exclude row's photo set and
    /// subtracts every Exclude row's (e.g. "everyone but me").</summary>
    public ObservableCollection<PlayerFilterRow> PlayerFilterCriteria { get; } = [];

    /// <summary>MainWindow's single filter-bar box can only ever represent one player at a
    /// time, so it's a compatibility view onto PlayerFilterCriteria rather than its own
    /// separate state: reading it returns the first non-empty row's option (or "(all players)"
    /// if there isn't one - see PlayerFilterPicker's CollapseWhenMultiple for how the box
    /// itself shows a "N players filtered" summary instead once there's more than one),
    /// and setting it (the only thing that box's picker can do) replaces the *entire* criteria
    /// list with just that one Include row, same as picking a single player always did before
    /// FilterWindow could add more.</summary>
    public PlayerFilterOption SelectedPlayerFilter
    {
        get => PlayerFilterCriteria.FirstOrDefault(r => !r.IsEmpty)?.Option ?? AllPlayersOption;
        set
        {
            PlayerFilterCriteria.Clear();
            if (value.VrcUserId is not null || value.PersonId is not null)
            {
                PlayerFilterCriteria.Add(NewPlayerFilterRow(value));
            }
            OnPlayerFilterCriteriaChanged();
        }
    }

    private PlayerFilterRow NewPlayerFilterRow(PlayerFilterOption option)
    {
        var row = new PlayerFilterRow(option);
        row.Changed += (_, _) => OnPlayerFilterCriteriaChanged();
        return row;
    }

    /// <summary>Keeps PlayerFilterCriteria shaped as "zero or more real (non-empty) rows,
    /// followed by exactly one empty row" - drops any empty row that isn't last (a row cleared
    /// back to "(all players)" in the middle of the list just disappears rather than leaving a
    /// gap), and appends a fresh empty one if the list is empty or its last row just became
    /// real. Called after every row change (PlayerFilterRow.Changed) and by
    /// RefreshPlayerFilterOptions at startup.</summary>
    private void EnsurePlayerFilterCriteriaShape()
    {
        for (int i = PlayerFilterCriteria.Count - 2; i >= 0; i--)
        {
            if (PlayerFilterCriteria[i].IsEmpty) PlayerFilterCriteria.RemoveAt(i);
        }
        if (PlayerFilterCriteria.Count == 0 || !PlayerFilterCriteria[^1].IsEmpty)
        {
            PlayerFilterCriteria.Add(NewPlayerFilterRow(AllPlayersOption));
        }
    }

    private void OnPlayerFilterCriteriaChanged()
    {
        EnsurePlayerFilterCriteriaShape();
        OnPropertyChanged(nameof(SelectedPlayerFilter));
        OnPropertyChanged(nameof(CanFilterTaggedOnly));
        RebuildRows();
    }

    /// <summary>FilterWindow's per-row remove ("x") button - explicit removal rather than just
    /// clearing the row back to "(all players)", since clearing it would leave it in place as
    /// the new trailing empty row when it's already the last one, or get silently dropped by
    /// EnsurePlayerFilterCriteriaShape's "no empty rows except the last" rule when it isn't -
    /// either way that's a confusing way to express "get rid of this filter" compared to a
    /// dedicated button.</summary>
    public void RemovePlayerFilterCriterion(PlayerFilterRow row)
    {
        PlayerFilterCriteria.Remove(row);
        OnPlayerFilterCriteriaChanged();
    }

    private List<PlayerFilterOption> _playerFilterOptions = [AllPlayersOption];
    public List<PlayerFilterOption> PlayerFilterOptions
    {
        get => _playerFilterOptions;
        private set { _playerFilterOptions = value; OnPropertyChanged(); }
    }

    /// <summary>The "Tagged only" checkbox is meaningless with no VRC-linked player required by
    /// any active row.</summary>
    public bool CanFilterTaggedOnly => PlayerFilterCriteria.Any(r => !r.IsEmpty && r.Option.VrcUserId is not null);

    private bool _taggedOnlyFilter;
    public bool TaggedOnlyFilter
    {
        get => _taggedOnlyFilter;
        set { _taggedOnlyFilter = value; OnPropertyChanged(); RebuildRows(); }
    }

    /// <summary>The "Own photos only" checkbox is meaningless (and disabled) if the local
    /// account's VRCX id can't be resolved - same reasoning as CanFilterTaggedOnly.</summary>
    public bool CanFilterOwnPhotosOnly => _selfUserId is not null;

    private bool _ownPhotosOnlyFilter;
    public bool OwnPhotosOnlyFilter
    {
        get => _ownPhotosOnlyFilter;
        set { _ownPhotosOnlyFilter = value; OnPropertyChanged(); RebuildRows(); }
    }

    private string _statusMessage = "Not logged in. Click Login to start.";
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    /// <summary>A brief, more noticeable pop-up notification than the status bar (which is easy
    /// to miss, especially mid-way through a batch action's own progress updates) - MainWindow
    /// subscribes once and animates a fade-in/hold/fade-out toast for whatever text comes
    /// through. Raised automatically by RecordActionSuccess for every long-running action's
    /// genuine completion (reuses that moment's StatusMessage text, so no per-action wiring is
    /// needed), and callable directly (ShowToast) for one-off UI feedback like a clipboard
    /// copy that isn't a RecordActionSuccess-tracked action.</summary>
    public event Action<string>? ToastRequested;

    public void ShowToast(string message) => ToastRequested?.Invoke(message);

    public ICommand ScanLibraryCommand { get; }
    public RelayCommand ScanFacesCommand { get; }
    public RelayCommand SuggestFacesCommand { get; }
    public RelayCommand ClassifyPhotosCommand { get; }
    public RelayCommand ClassifyAvatarsCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand SyncMetadataCommand { get; }
    public RelayCommand CrossReferenceGamelogCommand { get; }
    public ICommand SyncVrcPlayerDataCommand { get; }
    public RelayCommand UploadSelectedCommand { get; }
    public RelayCommand RemoveFromVrcdnCommand { get; }
    public ICommand CropPrintSelectedCommand { get; }
    public RelayCommand DeselectAllCommand { get; }

    /// <summary>Drives the Deselect button's "Deselect {n} photos" label - kept up to date by
    /// RaiseSelectionDependentCommands, the same place Upload/Remove-from-VRCDN's CanExecute
    /// already gets re-evaluated on every selection change.</summary>
    public int SelectedCount => _allPhotos.Count(p => p.Selected);

    private Task DeselectAllAsync()
    {
        foreach (var photo in _allPhotos.Where(p => p.Selected).ToList())
        {
            photo.Selected = false;
        }
        return Task.CompletedTask;
    }

    /// <summary>Appends a "Last succeeded: ..." (or "Never run yet.") line to an action
    /// button's static explanation text - baseText stays authored here rather than in XAML so
    /// it can be combined with the live timestamp in one bound string, instead of needing a
    /// converter or a multi-element ToolTip template repeated on every button.</summary>
    private bool HasSucceeded(string actionKey) => _repo.GetLastActionSuccess(actionKey) is not null;

    /// <summary>requiredActionKey, if given, is a prerequisite this button is gated on (see the
    /// RelayCommand CanExecute predicates in the constructor) - when it hasn't succeeded yet,
    /// the tooltip explains why the button is disabled instead of just showing "Never run yet."
    /// for this action with no indication of what to do about it.</summary>
    private string GetActionTooltip(string actionKey, string baseText, (string RequiredActionKey, string RequiredActionLabel)? requires = null)
    {
        DateTime? last = _repo.GetLastActionSuccess(actionKey);
        string lastLine = last is DateTime d ? $"Last succeeded: {d.ToLocalTime():g}" : "Never run yet.";
        string gateLine = requires is { } r && !HasSucceeded(r.RequiredActionKey)
            ? $"\n\nRun {r.RequiredActionLabel} first."
            : "";
        return $"{baseText}\n\n{lastLine}{gateLine}";
    }

    /// <summary>Records this action's success (for the tooltip line above), notifies the
    /// corresponding Tooltip property so a currently-open or next-shown tooltip reflects it, and
    /// pops a toast with whatever StatusMessage that action already set as its final
    /// human-readable completion text (e.g. "Face scan complete: 12 faces found across 340
    /// photos.") - free toast coverage for every long-running action without touching each one's
    /// individual code. Called once, right at each action method's genuine completion point -
    /// never from an early "unavailable"/cancelled/nothing-to-do return, so a button that never
    /// actually ran keeps showing "Never run yet." instead of a misleading timestamp (and
    /// doesn't pop a toast for doing nothing).</summary>
    private void RecordActionSuccess(string actionKey, string tooltipPropertyName)
    {
        _repo.RecordActionSuccess(actionKey);
        OnPropertyChanged(tooltipPropertyName);
        ShowToast(StatusMessage);
    }

    public string LoginTooltip => GetActionTooltip("Login",
        "Sign in to panel.vrcdn.live in an embedded browser.\nNeeded before you can upload, sync, or remove photos on VRCDN.");
    public string ScanLibraryTooltip => GetActionTooltip("ScanLibrary",
        "Look for new or changed photos in your VRChat screenshots folder.\nReads world/author/player info from VRCX and builds thumbnails.");
    public string CrossReferenceGamelogTooltip => GetActionTooltip("CrossReferenceGamelog",
        "For photos with no VRCX player data at all, e.g. taken by someone else nearby.\nGuesses who was present by matching the photo's timestamp against your own VRCX gamelog.",
        ("ScanLibrary", "Scan Library"));
    public string SyncVrcPlayerDataTooltip => GetActionTooltip("SyncVrcPlayerData",
        "Refresh the player cache from VRCX's friends list and gamelog history.\nTag Faces search uses this cache instead of querying VRCX live, since that got slow with a large gamelog.\nRun this occasionally to pick up new friends, renames, or people you've recently played with.");
    public string ScanFacesTooltip => GetActionTooltip("DetectFaces",
        "Detect anime-style faces in every photo and show a count badge on each thumbnail.",
        ("ScanLibrary", "Scan Library"));
    public string SuggestFacesTooltip => GetActionTooltip("SuggestFaces",
        "Suggest who's in each untagged face by comparing it to people's confirmed reference photos.\nSuggestions aren't automatic - review and confirm them in Tag Faces.",
        ("DetectFaces", "Detect Faces"));
    public string ClassifyPhotosTooltip => GetActionTooltip("ClassifyPhotos",
        "Rate any unrated photo using a local WD14 classifier.",
        ("ScanLibrary", "Scan Library"));
    public string ClassifyAvatarsTooltip => GetActionTooltip("ClassifyAvatars",
        "Detect the avatar base worn in each unclassified photo using the downloaded avatar classifier model.",
        ("ScanLibrary", "Scan Library"));
    public string CropPrintSelectedTooltip => GetActionTooltip("CropPrintBorders",
        "For selected Print-format (2048x1440) photos: crop off the white border and add the 1920x1080 result to your library.\nThe original is left untouched.");
    public string UploadSelectedTooltip => GetActionTooltip("UploadSelected",
        "Upload all selected photos that aren't already on VRCDN.");
    public string SyncMetadataTooltip => GetActionTooltip("SyncVrcdnMetadata",
        "Check which photos are already uploaded to VRCDN and fix their status badge.\nFor VRCX metadata (author/world/players), use Scan Library instead.");
    public string RemoveFromVrcdnTooltip => GetActionTooltip("RemoveFromVrcdn",
        "Delete selected photos from VRCDN's storage and mark them Not Uploaded here.");

    /// <summary>Exposed so the Settings window (opened from code-behind, like AboutWindow/
    /// MetadataWindow) can read/write the WD14 model path settings.</summary>
    public PhotoRepository Repo => _repo;

    /// <summary>Exposed so MainWindow's code-behind can open TagFacesWindow (opened from
    /// code-behind, like MetadataWindow/SettingsWindow).</summary>
    public FaceRepository Faces => _faces;
    public AvatarRegionRepository AvatarRegions => _avatarRegions;
    public AvatarTypeService? AvatarClassifier => _avatarClassifier;
    public VrcxProfileLookupService? ProfileLookup => _profileLookup;

    /// <summary>Read fresh (not cached) each time - it's a single cheap SQLite lookup, so
    /// there's no need for a separate "reload after Settings closes" step.</summary>
    public bool AutoCopyUrlOnHover => _repo.GetBoolSetting(SettingsKeys.AutoCopyVrcdnUrlOnHover);

    /// <summary>Same "read fresh, no reload step needed" reasoning as AutoCopyUrlOnHover -
    /// MainWindow's ResetHoverTimer reads this on every MouseEnter rather than caching it once
    /// at startup, so a change in Settings takes effect on the very next hover instead of
    /// needing an app restart.</summary>
    public double HoverPreviewDelaySeconds => _repo.GetDoubleSetting(SettingsKeys.HoverPreviewDelaySeconds, 0.25);

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
        // Restored from the previous session - straight into the backing field (not the
        // ThumbnailSize property) since its setter calls RebuildRows, which has nothing to
        // rebuild yet (this runs before InitializeAsync loads any photos) and would just be
        // redundant work once it does.
        _thumbnailSize = _repo.GetDoubleSetting(SettingsKeys.LastThumbnailSize, _thumbnailSize);
        _thumbnails = new ThumbnailService();
        _credentials = new CredentialStore(_repo);
        _faces = new FaceRepository(Path.Combine(dataDir, "vrcdn_manager.db"));
        _avatarRegions = new AvatarRegionRepository(Path.Combine(dataDir, "vrcdn_manager.db"));

        ScanLibraryCommand = new RelayCommand(ScanLibraryAsync);
        // Gated on ScanLibrary/DetectFaces having succeeded at least once (not just this
        // session - GetLastActionSuccess is persisted, so a library scanned in an earlier
        // session correctly leaves these enabled on the next launch too), in addition to the
        // existing model-availability checks. RaiseCanExecuteChanged for these fires right
        // after their prerequisite's RecordActionSuccess call, so they unlock immediately
        // within the same session instead of needing a restart.
        ScanFacesCommand = new RelayCommand(ScanFacesAsync,
            () => _faceDetector is not null && HasSucceeded("ScanLibrary"));
        SuggestFacesCommand = new RelayCommand(SuggestFacesAsync,
            () => _clipEmbedder is not null && HasSucceeded("DetectFaces"));
        ClassifyPhotosCommand = new RelayCommand(ClassifyPhotosAsync,
            () => _tagger is not null && HasSucceeded("ScanLibrary"));
        ClassifyAvatarsCommand = new RelayCommand(ClassifyAvatarsAsync,
            () => _avatarClassifier is not null && HasSucceeded("ScanLibrary"));
        LoginCommand = new RelayCommand(LoginAsync);
        SyncMetadataCommand = new RelayCommand(SyncMetadataAsync);
        CrossReferenceGamelogCommand = new RelayCommand(CrossReferenceGamelogAsync, () => HasSucceeded("ScanLibrary"));
        SyncVrcPlayerDataCommand = new RelayCommand(SyncVrcPlayerDataAsync);
        UploadSelectedCommand = new RelayCommand(UploadSelectedAsync, CanUploadSelected);
        RemoveFromVrcdnCommand = new RelayCommand(RemoveFromVrcdnAsync, CanRemoveFromVrcdn);
        CropPrintSelectedCommand = new RelayCommand(CropPrintSelectedAsync);
        DeselectAllCommand = new RelayCommand(DeselectAllAsync, () => _allPhotos.Any(p => p.Selected));
        EnsurePlayerFilterCriteriaShape();

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
        RefreshUploadCropModeFilterOptions();

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

        var (avatarClassifier, avatarError) = await Task.Run(() =>
        {
            string? modelDir = ResolveAvatarModelDir();
            if (modelDir is null) return (null, "Avatar model directory not configured (set it via Settings).");
            var s = AvatarTypeService.TryCreate(modelDir, out string? error);
            return (s, error);
        });
        _avatarClassifier = avatarClassifier;
        ClassifyAvatarsCommand.RaiseCanExecuteChanged();
        if (_avatarClassifier is null)
        {
            StatusMessage = $"Avatar classifier unavailable: {avatarError}";
        }

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
        _selfUserId = _profileLookup?.GetSelf()?.UserId;
        OnPropertyChanged(nameof(CanFilterOwnPhotosOnly));

        var (clipEmbedder, clipError) = await Task.Run(() =>
        {
            string? modelDir = ResolveClipModelDir();
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
        RecordActionSuccess("Login", nameof(LoginTooltip));
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
        DeselectAllCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedCount));
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
                    || (existing.Model.AuthorDisplayName is not null && existing.Model.AuthorId is null)
                    || (existing.Model.WorldName is not null && existing.Model.WorldId is null);
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
                        var players = meta?.Players?.Select(p => (p.Id, p.DisplayName));
                        _repo.SetVrcxMetadata(id, meta?.Author?.Id, meta?.Author?.DisplayName, meta?.World?.Name, meta?.World?.Id, players);
                        existing.Model.MetadataScanned = true;
                        existing.Model.AuthorId = meta?.Author?.Id;
                        existing.Model.AuthorDisplayName = meta?.Author?.DisplayName;
                        existing.Model.WorldName = meta?.World?.Name;
                        existing.Model.WorldId = meta?.World?.Id;
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
        RecordActionSuccess("ScanLibrary", nameof(ScanLibraryTooltip));
        // Unlocks the 4 buttons gated on ScanLibrary having run at least once, and refreshes
        // their tooltips so the "Run Scan Library first." note disappears immediately instead
        // of only on the next hover after WPF's own requery happens to fire.
        ScanFacesCommand.RaiseCanExecuteChanged();
        ClassifyPhotosCommand.RaiseCanExecuteChanged();
        ClassifyAvatarsCommand.RaiseCanExecuteChanged();
        CrossReferenceGamelogCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ScanFacesTooltip));
        OnPropertyChanged(nameof(ClassifyPhotosTooltip));
        OnPropertyChanged(nameof(ClassifyAvatarsTooltip));
        OnPropertyChanged(nameof(CrossReferenceGamelogTooltip));
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
        // A fully-resolved photo (already scanned, and every detection it found is now tagged,
        // marked <unknown>, or deleted - e.g. via TagFacesWindow's "All tagged" button) has
        // nothing left for the detector to find, so re-running it there is wasted ML inference.
        // Opt-out via Settings (SkipResolvedPhotosOnFaceScan) for a deliberate full rescan, e.g.
        // after swapping in a better detector model.
        if (_repo.GetBoolSetting(SettingsKeys.SkipResolvedPhotosOnFaceScan, true))
        {
            var unresolvedIds = _faces.GetPhotoIdsWithUnresolvedFaces();
            photos = photos.Where(p => !p.Model.FacesScanned || unresolvedIds.Contains(p.Model.Id)).ToList();
        }

        int processed = 0, totalFaces = 0;
        foreach (var vm in photos)
        {
            try
            {
                var faces = await Task.Run(() => _faceDetector.DetectFaces(vm.Model.LocalPath));
                _faces.InsertDetectedFaces(vm.Model.Id, faces);
                _repo.SetFacesScanned(vm.Model.Id);
                vm.Model.FacesScanned = true;
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
        RecordActionSuccess("DetectFaces", nameof(ScanFacesTooltip));
        SuggestFacesCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(SuggestFacesTooltip));
    }

    /// <summary>
    /// Fallback for photos missing VRCX-embedded data - player list and/or world name - by
    /// cross-referencing the local VRCX account's own gamelog instead. See
    /// GamelogCorrelationService and
    /// docs/superpowers/specs/2026-08-01-gamelog-player-inference-design.md (players) and
    /// docs/superpowers/specs/2026-08-02-gamelog-world-and-avatar-backfill-design.md (world).
    /// Deliberately opt-in via its own button rather than folded into Scan Library: it depends
    /// on this account's gamelog actually covering the photo's capture time, which won't always
    /// be true (VRCX closed, a gap in the log, a photo from before this account's records
    /// start), so it shouldn't run silently as part of the normal scan.
    /// </summary>
    private async Task CrossReferenceGamelogAsync()
    {
        using var gamelog = GamelogCorrelationService.TryCreate(out string? gamelogError);
        if (gamelog is null)
        {
            StatusMessage = $"Gamelog cross-reference unavailable: {gamelogError}";
            return;
        }

        var missingPlayerIds = _repo.GetPhotoIdsMissingPlayerData();
        // Both sets feed the same TryGetWorld lookup below - GetPhotoIdsMissingWorldName covers
        // photos with no world name at all, GetPhotoIdsNeedingWorldIdBackfill covers photos
        // already gamelog-inferred before world_id was read from VRCX (see its doc comment) and
        // just need the id filled in.
        var missingWorldIds = _repo.GetPhotoIdsMissingWorldName().Union(_repo.GetPhotoIdsNeedingWorldIdBackfill()).ToHashSet();
        var missingIds = missingPlayerIds.Union(missingWorldIds).ToHashSet();
        var candidates = _allPhotos.Where(p => missingIds.Contains(p.Model.Id)).ToList();

        StatusMessage = "Cross-referencing gamelog...";
        int processed = 0, matched = 0;
        foreach (var vm in candidates)
        {
            if (GamelogCorrelationService.TryParseCaptureTime(vm.Model.LocalPath) is DateTime time)
            {
                bool matchedAnything = false;

                if (missingPlayerIds.Contains(vm.Model.Id))
                {
                    var players = await Task.Run(() => gamelog.FindPresentPlayers(time));
                    if (players is { Count: > 0 })
                    {
                        _repo.InsertGamelogInferredPlayers(vm.Model.Id, players);
                        matchedAnything = true;
                    }
                }

                if (missingWorldIds.Contains(vm.Model.Id))
                {
                    var world = await Task.Run(() => gamelog.TryGetWorld(time));
                    if (world is not null)
                    {
                        _repo.SetWorldNameInferred(vm.Model.Id, world.Value.WorldName, world.Value.WorldId);
                        vm.Model.WorldName = world.Value.WorldName;
                        vm.Model.WorldId = world.Value.WorldId;
                        vm.Model.WorldNameInferred = true;
                        matchedAnything = true;
                    }
                }

                if (matchedAnything)
                {
                    matched++;
                    vm.NotifyMetadataChanged();
                }
            }

            processed++;
            if (processed % 25 == 0 || processed == candidates.Count)
            {
                StatusMessage = $"Cross-referencing gamelog... {processed}/{candidates.Count} photos, {matched} matched so far";
            }
        }

        ApplyPlayerCounts();
        StatusMessage = $"Gamelog cross-reference done: {matched}/{candidates.Count} photos matched (players and/or world name).";
        RecordActionSuccess("CrossReferenceGamelog", nameof(CrossReferenceGamelogTooltip));
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
        RecordActionSuccess("SyncVrcPlayerData", nameof(SyncVrcPlayerDataTooltip));
    }

    private async Task SuggestFacesAsync()
    {
        if (_clipEmbedder is null) { StatusMessage = "CLIP face-matching model not available."; return; }

        StatusMessage = "Computing face embeddings...";
        var pathById = _allPhotos.ToDictionary(p => p.Model.Id, p => p.Model.LocalPath);
        var needingEmbedding = _faces.GetDetectedFacesWithoutEmbedding();
        int embedded = 0;
        // See ClassifyPhotosAsync for why bounded concurrency here is safe: ClipEmbeddingService
        // serializes its own session.Run() calls internally, so only the CPU-bound
        // preprocessing overlaps across threads.
        using var embedSemaphore = new SemaphoreSlim(Environment.ProcessorCount);
        var embedTasks = needingEmbedding.Select(async face =>
        {
            if (!pathById.TryGetValue(face.PhotoId, out string? path)) return;
            await embedSemaphore.WaitAsync();
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
            finally
            {
                embedSemaphore.Release();
            }

            embedded++;
            if (embedded % 25 == 0 || embedded == needingEmbedding.Count)
            {
                StatusMessage = $"Computing face embeddings... {embedded}/{needingEmbedding.Count}";
            }
        });
        await Task.WhenAll(embedTasks);

        StatusMessage = "Building reference centroids...";
        var persons = _faces.GetAllPersons();
        var centroids = new Dictionary<long, float[]>();
        var confirmedPhotoIdsByPerson = new Dictionary<long, HashSet<long>>();
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
            if (centroid is not null)
            {
                centroids[person.Id] = centroid;
                confirmedPhotoIdsByPerson[person.Id] = _faces.GetTaggedPhotoIdsForPerson(person.Id);
            }
        }

        if (centroids.Count == 0)
        {
            StatusMessage = $"No registered person has enough reference photos yet (need >= {FaceMatcher.MinReferenceEmbeddings}: profile picture + confirmed tags combined).";
            return;
        }

        StatusMessage = "Matching faces against registered people...";
        var avatarTypeByPhotoId = _allPhotos.ToDictionary(p => p.Model.Id, p => p.Model.AvatarType);
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

            if (!accept) continue;

            // Avatar-affinity boost: does this photo's AvatarType appear anywhere in the best
            // candidate's own confirmed photos? No confident AvatarType on this photo, or no overlap,
            // means zero boost - never a penalty (see Global Constraints).
            float avatarAffinityBoost = 0f;
            if (avatarTypeByPhotoId.TryGetValue(face.PhotoId, out string? thisPhotoAvatarType)
                && thisPhotoAvatarType is not null
                && confirmedPhotoIdsByPerson.TryGetValue(best.PersonId, out var bestPersonPhotoIds)
                && bestPersonPhotoIds.Any(pid => avatarTypeByPhotoId.TryGetValue(pid, out string? knownType) && knownType == thisPhotoAvatarType))
            {
                avatarAffinityBoost = FaceMatcher.AvatarAffinityBoost;
            }

            // Co-occurrence boost: exactly one other person already confirmed in this photo, zero
            // other undetermined faces remaining, and that pair has been confirmed together enough
            // times before to trust it as a real pattern rather than one coincidental photo.
            float coOccurrenceBoost = 0f;
            if (_faces.GetUndeterminedFaceCountInPhoto(face.PhotoId, face.Id) == 0)
            {
                var otherConfirmedPersonIds = _faces.GetConfirmedPersonIdsInPhoto(face.PhotoId, face.Id);
                if (otherConfirmedPersonIds.Count == 1
                    && confirmedPhotoIdsByPerson.TryGetValue(best.PersonId, out var bestIds)
                    && confirmedPhotoIdsByPerson.TryGetValue(otherConfirmedPersonIds[0], out var otherIds)
                    && bestIds.Intersect(otherIds).Count() >= FaceMatcher.MinCoOccurrenceCount)
                {
                    coOccurrenceBoost = FaceMatcher.CoOccurrenceBoost;
                }
            }

            float combinedScore = confidence + avatarAffinityBoost + coOccurrenceBoost;
            // AutoTagThreshold is calibrated against the margin scale (DifferentialMarginThreshold-based);
            // a single-candidate raw similarity is a different scale entirely and would always exceed it,
            // so single-candidate suggestions are capped at ConfirmPrompt regardless of score.
            SuggestionTier tier = scored.Count > 1 ? FaceMatcher.DetermineTier(combinedScore) : SuggestionTier.ConfirmPrompt;
            FaceLabelSource source = tier == SuggestionTier.AutoTagged ? FaceLabelSource.AutoTagged : FaceLabelSource.EmbeddingMatch;

            _faces.UpsertFaceLabel(face.Id, best.PersonId, confirmed: false, source, combinedScore);
            _faces.UpsertSuggestionLog(face.Id, best.PersonId, combinedScore, confidence, avatarAffinityBoost, coOccurrenceBoost, tier);
            suggested++;
        }

        StatusMessage = $"Suggest Faces done: {embedded} embeddings computed, {suggested} new suggestions across {centroids.Count} eligible people.";
        RecordActionSuccess("SuggestFaces", nameof(SuggestFacesTooltip));
    }

    /// <summary>Rebuilds the player-filter dropdown from the current library state - called
    /// once at startup and again after the Tag Faces window closes, so newly-tagged people
    /// show their current tag/photo counts without needing a full app restart.</summary>
    public void RefreshPlayerFilterOptions()
    {
        var taggedCounts = _faces.GetTaggedUserTagCounts();
        var presentCounts = _repo.GetPresentPhotoCountsByUser();

        // "(tagged/in-photos)" - confirmed face tags vs. total photos they're present in
        // (PhotoPlayers or as author - see GetPresentPhotoCountsByUser). Every VRCX-sourced
        // player here has at least one PhotoPlayers row (that's how GetDistinctPlayers found
        // them), so the second number is never 0; the first commonly is, for anyone not yet
        // tagged.
        var vrcxPlayers = _repo.GetDistinctPlayers().Select(p =>
            (Name: p.DisplayName, Option: new PlayerFilterOption(p.UserId, null,
                $"{p.DisplayName} ({taggedCounts.GetValueOrDefault(p.UserId)}/{presentCounts.GetValueOrDefault(p.UserId)})")));

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
    /// exe (so a public build works for anyone who drops the model there), then the stable
    /// %LOCALAPPDATA% default (see DefaultModelPaths - works across app updates/packaging),
    /// falling back to the dev machine's path this was built against.
    /// </summary>
    private string ResolveWdTaggerModelDir()
    {
        string? configured = _repo.GetStringSetting(SettingsKeys.WdModelDir);
        if (configured is not null && Directory.Exists(configured)) return configured;

        string local = Path.Combine(AppContext.BaseDirectory, "wd14-model");
        if (Directory.Exists(local)) return local;

        if (Directory.Exists(DefaultModelPaths.WdTagger)) return DefaultModelPaths.WdTagger;

        return @"D:\AI-Tools\wd14-tagger\model";
    }

    private string? ResolveClipModelDir()
    {
        string? configured = _repo.GetStringSetting(SettingsKeys.ClipModelDir);
        if (configured is not null && Directory.Exists(configured)) return configured;
        return Directory.Exists(DefaultModelPaths.Clip) ? DefaultModelPaths.Clip : null;
    }

    private string? ResolveAvatarModelDir()
    {
        string? configured = _repo.GetStringSetting(SettingsKeys.AvatarModelDir);
        if (configured is not null && Directory.Exists(configured)) return configured;
        return Directory.Exists(DefaultModelPaths.Avatar) ? DefaultModelPaths.Avatar : null;
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
        // Bounded to core count: the actual bottleneck is CPU-side preprocessing (image
        // decode + resize), not the GPU inference call itself - WdTaggerService serializes
        // its own session.Run() calls internally now (seeing concurrent Run() calls on a
        // DirectML session natively crash the process - see
        // feedback-onnx-directml-concurrency-crash memory), so it's safe to run several
        // photos' preprocessing concurrently here. Safe without explicit locking around the
        // UI-touching code below despite the concurrency: nothing here uses
        // ConfigureAwait(false), so every `await Task.Run(...)` resumes back on the UI
        // thread's captured SynchronizationContext - the WPF Dispatcher serializes those
        // resumptions, so the vm/_repo/StatusMessage/`done` updates never actually run at
        // the same instant even though several photos' heavy work overlaps in the pool.
        using var semaphore = new SemaphoreSlim(Environment.ProcessorCount);
        var tasks = toClassify.Select(async vm =>
        {
            await semaphore.WaitAsync();
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
            finally
            {
                semaphore.Release();
            }

            done++;
            if (done % 20 == 0 || done == toClassify.Count)
            {
                StatusMessage = $"Classifying... {done}/{toClassify.Count}";
            }
        });
        await Task.WhenAll(tasks);

        StatusMessage = $"Classified {done} photos.";
        RecordActionSuccess("ClassifyPhotos", nameof(ClassifyPhotosTooltip));
        RebuildRows();
    }

    /// <summary>Runs the avatar-type classifier in-process for any photo not yet classified
    /// (AvatarTypeConfidence is null) PLUS any photo previously scored "no confident match"
    /// (AvatarTypeConfidence set, AvatarType null) - Plan A's label set grows over time as its
    /// pipeline is re-run and republished, so a photo that missed against today's model is
    /// worth retrying once a bigger model is downloaded, not permanently skipped like
    /// ClassifyPhotosAsync's "already has a value" photos.</summary>
    private async Task ClassifyAvatarsAsync()
    {
        if (_avatarClassifier is null) { StatusMessage = "Avatar classifier not available."; return; }

        var missingIds = _repo.GetPhotoIdsMissingAvatarType();
        var retryIds = _repo.GetPhotoIdsWithNoConfidentMatch();
        var toClassify = _allPhotos.Where(p => missingIds.Contains(p.Model.Id) || retryIds.Contains(p.Model.Id)).ToList();
        if (toClassify.Count == 0) { StatusMessage = "Nothing to classify - every photo already has an avatar-type result."; return; }

        int done = 0, failed = 0;
        // See ClassifyPhotosAsync for why bounded concurrency here is safe: AvatarTypeService
        // serializes its own session.Run() calls internally, so only the CPU-bound
        // preprocessing overlaps across threads.
        using var semaphore = new SemaphoreSlim(Environment.ProcessorCount);
        var tasks = toClassify.Select(async vm =>
        {
            await semaphore.WaitAsync();
            try
            {
                var (label, catalogId, confidence) = await Task.Run(() => _avatarClassifier.Classify(vm.Model.LocalPath));
                vm.Model.AvatarType = label;
                vm.Model.AvatarCatalogId = catalogId;
                vm.Model.AvatarTypeConfidence = confidence;
                _repo.SetAvatarType(vm.Model.Id, label, catalogId, confidence);
                vm.NotifyAvatarTypeChanged();
            }
            catch (Exception ex)
            {
                failed++;
                StatusMessage = $"Avatar classification failed for {vm.FileName}: {ex.Message}";
            }
            finally
            {
                semaphore.Release();
            }

            done++;
            if (done % 20 == 0 || done == toClassify.Count)
            {
                StatusMessage = $"Classifying avatars... {done}/{toClassify.Count}";
            }
        });
        await Task.WhenAll(tasks);

        StatusMessage = failed > 0
            ? $"Classified {done - failed} photos' avatar types ({failed} failed)."
            : $"Classified {done} photos' avatar types.";
        RecordActionSuccess("ClassifyAvatars", nameof(ClassifyAvatarsTooltip));
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
        RecordActionSuccess("CropPrintBorders", nameof(CropPrintSelectedTooltip));
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
        RecordActionSuccess("SyncVrcdnMetadata", nameof(SyncMetadataTooltip));
    }

    private async Task UploadSelectedAsync()
    {
        if (_api is null) { StatusMessage = "Log in first."; return; }

        var toUpload = _allPhotos.Where(p => p.Selected && p.RemoteStatus != RemoteStatus.Uploaded).ToList();
        if (toUpload.Count == 0) { StatusMessage = "Nothing selected to upload."; return; }

        if (!TryResolveUploadCropAspectRatio(out double? cropRatio, out string? cropError))
        {
            StatusMessage = cropError!;
            return;
        }
        string? cropModeLabel = ResolveUploadCropModeLabel(cropRatio);

        // Fetched once, not per-photo - only used to build the RemoteUrl string below, which
        // is the same for every photo in this batch.
        string username = await _api.GetUsernameAsync();

        int done = 0;
        foreach (var vm in toUpload)
        {
            vm.Model.RemoteStatus = RemoteStatus.Uploading;
            vm.RefreshStatus();
            _repo.UpdateRemoteStatus(vm.Model.Id, RemoteStatus.Uploading);

            try
            {
                var (resized, width, height) = await _thumbnails.PrepareForUploadAsync(vm.Model.LocalPath, cropRatio);
                // Only a cropped upload gets a resolution suffix - an uncropped one keeps its
                // filename exactly as before crop-on-upload existed, so existing uploads/
                // filename-matching (SyncRemoteMatches) aren't affected.
                string baseFileName = Path.GetFileNameWithoutExtension(vm.FileName);
                string uploadFileName = cropRatio is not null ? $"{baseFileName}_{width}x{height}.jpg" : $"{baseFileName}.jpg";
                await _api.UploadBytesAsync(uploadFileName, resized);
                vm.Model.RemoteStatus = RemoteStatus.Uploaded;
                vm.Model.UploadedAt = DateTime.UtcNow.ToString("o");
                vm.Model.UploadCropMode = cropModeLabel;
                _repo.UpdateRemoteStatus(vm.Model.Id, RemoteStatus.Uploaded, uploadedAt: vm.Model.UploadedAt);
                _repo.SetUploadCropMode(vm.Model.Id, cropModeLabel);

                // UploadBytesAsync only returns a job id, not the object's final id/URL - VRCDN
                // resolves that asynchronously, AND reformats the filename server-side
                // (confirmed live: this app's own upload of
                // "VRChat_2026-07-11_23-30-14.050_7680x4320.jpg" came back from ListObjects as
                // "vrchat_20260711_233014050_7680x4320.jpg"), so a naive exact-name lookup can
                // never find it - reuse SyncRemoteMatches' real matching logic instead, run
                // right after this photo's own upload rather than deferred to the trailing
                // batch-wide sync. Best-effort: if VRCDN hasn't finished processing the upload
                // yet, this just doesn't resolve it this pass, and the trailing
                // SyncMetadataAsync call below still catches it as a fallback.
                var remoteObjects = await _api.ListObjectsAsync();
                _repo.SyncRemoteMatches(remoteObjects.Select(o => (o.Original, o.Id, o.Extension, o.Size)), username);
                (vm.Model.RemoteUrl, vm.Model.RemoteId) = _repo.GetRemoteInfo(vm.Model.Id);

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
        RecordActionSuccess("UploadSelected", nameof(UploadSelectedTooltip));
        RaiseSelectionDependentCommands();
        RefreshUploadCropModeFilterOptions();
        // Fallback safety net for anything the per-photo lookup above missed (VRCDN still
        // processing at the time), not the primary resolution path anymore.
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
        RecordActionSuccess("RemoveFromVrcdn", nameof(RemoveFromVrcdnTooltip));
        RaiseSelectionDependentCommands();
    }

    private void RebuildRows()
    {
        int columns = Math.Max(1, (int)(_gridWidth / (_thumbnailSize + RowMargin)));
        var filtered = GetFilteredSortedPhotos();

        Application.Current.Dispatcher.Invoke(() =>
        {
            Rows.Clear();
            foreach (var chunk in Chunk(filtered, columns))
            {
                Rows.Add(new PhotoRow(chunk));
            }
        });
    }

    /// <summary>Rebuilds Rows the same way RebuildRows does, but with leadingBlankCount null
    /// entries prepended to the flat filtered/sorted list before chunking - all real photos
    /// stay grouped with the same neighbors they'd normally have, just shifted as a block. Only
    /// row 0 ever ends up with blanks (leadingBlankCount is always less than the column count -
    /// see MainWindow's Alt+scroll handler, the only caller), so every row after the first is
    /// unaffected. Used to keep the photo under the cursor roughly in place across a thumbnail-
    /// size resize, where the plain unpadded rebuild that ThumbnailSize's setter already
    /// triggers would otherwise reflow which row/column a given photo lands in.</summary>
    public void RebuildRowsWithLeadingPadding(int leadingBlankCount)
    {
        int columns = Math.Max(1, (int)(_gridWidth / (_thumbnailSize + RowMargin)));
        var filtered = GetFilteredSortedPhotos();

        var padded = new List<object?>(leadingBlankCount + filtered.Count);
        padded.AddRange(Enumerable.Repeat<object?>(null, leadingBlankCount));
        padded.AddRange(filtered);

        Application.Current.Dispatcher.Invoke(() =>
        {
            Rows.Clear();
            foreach (var chunk in Chunk(padded, columns))
            {
                Rows.Add(new PhotoRow(chunk));
            }
        });
    }

    private List<PhotoViewModel> GetFilteredSortedPhotos()
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
        if (UploadCropModeFilter != "Any")
        {
            filtered = UploadCropModeFilter == UploadCropModeOriginal
                ? filtered.Where(p => p.RemoteStatus == RemoteStatus.Uploaded && p.Model.UploadCropMode is null)
                : filtered.Where(p => p.Model.UploadCropMode == UploadCropModeFilter);
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
        if (OwnPhotosOnlyFilter && _selfUserId is not null)
        {
            filtered = filtered.Where(p => p.Model.AuthorId == _selfUserId);
        }
        // Every non-empty row narrows further: an Include row intersects (the photo must have
        // THIS player too, on top of whatever earlier rows already required), an Exclude row
        // subtracts (the photo must NOT have this player - e.g. "everyone but me"). Order
        // between rows doesn't matter since each is applied as its own independent Where.
        foreach (var row in PlayerFilterCriteria.Where(r => !r.IsEmpty))
        {
            if (row.Option.VrcUserId is string userId)
            {
                // "Tagged only" stands on its own - it must NOT further narrow the VRCX-presence
                // set below, since a confirmed face tag can exist on a photo VRCX never matched a
                // player to at all (e.g. a manually-drawn box, or metadata scanning missed them).
                // Requiring both would silently hide correctly-tagged photos (found via a real
                // report: Sayakiss tagged on a photo with zero photo_players rows).
                var photoIds = TaggedOnlyFilter
                    ? _faces.GetTaggedPhotoIdsForUser(userId)
                    : _repo.GetPhotoIdsForUser(userId);
                filtered = row.Exclude
                    ? filtered.Where(p => !photoIds.Contains(p.Model.Id))
                    : filtered.Where(p => photoIds.Contains(p.Model.Id));
            }
            else if (row.Option.PersonId is long personId)
            {
                // Manual person - no VRCX presence data to filter from, so "selected" already
                // means "show their tagged photos" (see GetTaggedPhotoIdsForPerson).
                var taggedPhotoIds = _faces.GetTaggedPhotoIdsForPerson(personId);
                filtered = row.Exclude
                    ? filtered.Where(p => !taggedPhotoIds.Contains(p.Model.Id))
                    : filtered.Where(p => taggedPhotoIds.Contains(p.Model.Id));
            }
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

        return filtered.ToList();
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

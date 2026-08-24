using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using VrcPhotoManager.Data;
using VrcPhotoManager.Models;
using VrcPhotoManager.Services;

namespace VrcPhotoManager.Views;

public partial class TagFacesWindow : Window
{
    private readonly FaceRepository _faces;
    private readonly PhotoRepository _photos;
    private readonly AvatarRegionRepository _avatarRegionRepo;
    private readonly AvatarCatalogRepository _avatarCatalogRepo;
    private readonly PhotoSourceResolver _photoSourceResolver;
    private readonly AvatarTypeService? _avatarClassifier;
    private readonly VrcxProfileLookupService? _profileLookup;
    private Photo _photo;

    // Incremental "refresh suggestions for what's in view" banner (see MarkSuggestionsStale) -
    // all null/empty when the caller didn't wire this up (e.g. App.xaml.cs's headless
    // diagnostic), in which case the banner simply never shows. _setSuggestionsStale round-trips
    // staleness to MainViewModel so it survives this window being closed and a fresh one opened
    // for the next photo (this window is a singleton, fully re-created each time - see
    // MainWindow.OpenTagFaces).
    private readonly CcipEmbeddingService? _ccipEmbedder;
    private readonly List<long> _scopedPhotoIds;
    private readonly IReadOnlyDictionary<long, string> _pathByPhotoId;
    private readonly IReadOnlyDictionary<long, string?> _avatarTypeByPhotoId;
    private readonly Action<bool>? _setSuggestionsStale;
    private bool _isRefreshingSuggestions;

    private List<AvatarRegion> _avatarRegionsList = [];
    private long _activeAvatarRegionId;

    /// <summary>Same "backed out without tagging" cleanup as _pendingManualFaceId, for a
    /// freshly-drawn avatar region instead of a face box.</summary>
    private long? _pendingManualAvatarRegionId;

    private List<DetectedFace> _detectedFaces = [];
    private Dictionary<long, FaceLabel> _labelsByFaceId = [];
    private Dictionary<long, RegisteredPerson> _personsById = [];
    private List<PhotoPlayer> _photoPlayers = [];
    private List<GamelogInferredPlayer> _gamelogPlayers = [];
    private List<(string UserId, string DisplayName)> _friends = [];
    private List<(string UserId, string DisplayName)> _knownVrcUsers = [];
    private Dictionary<string, List<string>> _aliasesByUserId = [];
    private ObservableCollection<ManualPersonMergeSuggestion> _mergeSuggestions = [];
    private (string UserId, string DisplayName)? _self;
    private List<PickerItem> _staticPickerItems = [];
    private long _activeFaceId;
    private long? _renamingPersonId;
    private string? _editingAliasesForUserId;
    private double _fitZoomScale = 1.0;

    private bool _isPanning;
    private Point _panStartMousePosition;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;

    private bool _isDrawingBox;
    private Point _drawStartCanvasPoint;
    private Rectangle? _drawPreviewRect;

    /// <summary>Set right after a manually-drawn box is created and its picker opened; cleared
    /// by any action that resolves it (tag someone, mark &lt;unknown&gt;, explicit delete). If
    /// the popup closes while this is still set, the user backed out without choosing anything -
    /// PersonPickerPopup_Closed deletes the box rather than leaving an orphaned untagged one.</summary>
    private long? _pendingManualFaceId;

    /// <summary>Accumulates GetNotesAndBios results across keystrokes within one open person
    /// picker popup - re-querying VRCX's uncindexed, potentially huge _feed_bio table on every
    /// keystroke of NewPersonNameTextBox_TextChanged was a real, confirmed full-table-scan-per-
    /// keystroke perf problem (873 MB table, no index on user_id). Cleared in
    /// PersonPickerPopup_Closed - the picker is per-photo, so caching across different photos'
    /// pickers isn't wanted, but caching across keystrokes within one open popup is exactly the
    /// win.</summary>
    private Dictionary<string, VrcxProfileLookupService.NoteAndBio> _noteBioCache = [];

    /// <summary>
    /// RawName carries the actual primary name separate from DisplayText, which can have any
    /// number of parenthetical decorations appended ("(VRCX friend)", an alias list, etc.) -
    /// trying to recover the real name by string-Replace-ing every known suffix off DisplayText
    /// got fragile fast (5+ Replace calls, one per suffix format) and would only get worse once
    /// the alias list's content varies per item. EffectiveName is what callers should actually
    /// use; RawName is null (falls back to DisplayText, which IS the raw name) for items that
    /// were never given a suffix in the first place.
    /// </summary>
    private record PickerItem(string DisplayText, string? VrcUserId, long? ExistingPersonId, bool IsConfirmSuggestion = false, bool IsConfirmAutoTag = false, bool IsNotAFace = false, string? RawName = null, string? FriendGlyph = null)
    {
        public string EffectiveName => RawName ?? DisplayText;

        /// <summary>Rename (pencil) button only makes sense for an already-registered person
        /// with no linked VRC account - not the "confirm suggestion"/"&lt;unknown&gt;" pseudo-
        /// entries, not a bare VRCX player/friend row that hasn't been linked to a
        /// RegisteredPerson yet, and not a person who already has a known VRC username (their
        /// name comes from VRCX, so editing it here would just drift out of sync).</summary>
        public bool CanRename => ExistingPersonId is not null && VrcUserId is null && !IsConfirmSuggestion && !IsConfirmAutoTag && !IsNotAFace;

        /// <summary>The "+" alias button needs a real VRC user id to key aliases off of -
        /// available much more broadly than CanRename (any friend/gamelog/cached/registered
        /// entry with a VrcUserId, not just already-registered manual people).</summary>
        public bool CanEditAliases => VrcUserId is not null && !IsConfirmSuggestion && !IsConfirmAutoTag && !IsNotAFace;

        /// <summary>Populated after construction (see WithNoteTooltips) from a live,
        /// per-popup VRCX lookup - not a constructor parameter, since none of the 8 call
        /// sites that build PickerItems know about notes/bios individually. Null (no
        /// tooltip shown - WPF suppresses a null ToolTip binding) when VrcUserId is null or
        /// VRCX has neither a note nor a bio for this person.</summary>
        public string? NoteTooltip { get; init; }

        /// <summary>True when this row's person already has a Confirmed FaceLabel on some
        /// OTHER face in this same photo (see WithNoteTooltips, which also stamps this) -
        /// bolds the row so the "who is left to tag" list is easy to scan for names that
        /// still need a click versus ones already accounted for elsewhere in the photo.</summary>
        public bool AlreadyConfirmedInPhoto { get; init; }
    }

    /// <summary>Single point where a built PickerItem list gets its NoteTooltip and
    /// AlreadyConfirmedInPhoto filled in - called from both places a suggestion list is
    /// assembled (OpenPicker's static list, NewPersonNameTextBox_TextChanged's search results)
    /// so per-row VRCX/confirmed-state lookups only ever need to know about "whatever's in this
    /// list", not which of the 8 construction sites they came from.</summary>
    private List<PickerItem> WithNoteTooltips(List<PickerItem> items)
    {
        // A row can be identified either by RegisteredPerson id (ExistingPersonId) or by raw
        // VrcUserId (VRCX player/friend/cached rows not yet linked to a RegisteredPerson) - a
        // confirmed label only ever carries a PersonId, so the VrcUserId set below is derived
        // from resolving each confirmed PersonId back to its person's VrcUserId (null for a
        // manually-created person, which just never matches a VrcUserId-only row).
        var confirmedPersonIds = _labelsByFaceId.Values
            .Where(l => l.Confirmed && l.PersonId is not null)
            .Select(l => l.PersonId!.Value)
            .ToHashSet();
        var confirmedVrcUserIds = confirmedPersonIds
            .Select(id => _personsById.TryGetValue(id, out var p) ? p.VrcUserId : null)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToHashSet();
        items = items.Select(i => i with
        {
            AlreadyConfirmedInPhoto = (i.ExistingPersonId is long personId && confirmedPersonIds.Contains(personId))
                || (i.VrcUserId is string userId && confirmedVrcUserIds.Contains(userId))
        }).ToList();

        if (_profileLookup is null) return items;
        var ids = items.Where(i => i.VrcUserId is not null).Select(i => i.VrcUserId!).Distinct().ToList();
        if (ids.Count == 0) return items;

        // Only query VRCX for ids this popup hasn't already looked up - see _noteBioCache.
        var uncachedIds = ids.Where(id => !_noteBioCache.ContainsKey(id)).ToList();
        if (uncachedIds.Count > 0)
        {
            var fresh = _profileLookup.GetNotesAndBios(uncachedIds);
            foreach (var (userId, nb) in fresh)
            {
                _noteBioCache[userId] = nb;
            }
        }

        return items.Select(i =>
        {
            if (i.VrcUserId is not string userId || !_noteBioCache.TryGetValue(userId, out var nb)) return i;
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(nb.Note)) lines.Add($"Note: {nb.Note}");
            if (!string.IsNullOrWhiteSpace(nb.Bio)) lines.Add($"Bio: {nb.Bio}");
            return lines.Count == 0 ? i : i with { NoteTooltip = string.Join("\n", lines) };
        }).ToList();
    }

    private record ManualPersonMergeSuggestion(long ManualPersonId, string ManualName, string VrcUserId, string VrcDisplayName);

    public TagFacesWindow(FaceRepository faces, PhotoRepository photos, AvatarRegionRepository avatarRegions,
        AvatarCatalogRepository avatarCatalog, PhotoSourceResolver photoSourceResolver,
        AvatarTypeService? avatarClassifier, VrcxProfileLookupService? profileLookup, Photo photo,
        CcipEmbeddingService? ccipEmbedder = null,
        IReadOnlyList<long>? scopedPhotoIds = null,
        IReadOnlyDictionary<long, string>? pathByPhotoId = null,
        IReadOnlyDictionary<long, string?>? avatarTypeByPhotoId = null,
        bool suggestionsMayBeStale = false,
        Action<bool>? setSuggestionsStale = null)
    {
        InitializeComponent();
        DialogWindowBehavior.HideMinimizeAndMaximizeButtons(this);
        // The person-picker Popup is a transparent, separately-hwnd'd child window - it taking
        // keyboard focus (e.g. typing a new name) can itself trigger Deactivated on this window
        // even though the user never clicked away, so skip the close while it's open.
        DialogWindowBehavior.CloseOnDeactivated(this,
            stillOpenGuard: () => PersonPickerPopup.IsOpen || AvatarPickerPopup.IsOpen);
        DialogWindowBehavior.OpenNearCursor(this);
        _faces = faces;
        _photos = photos;
        _avatarRegionRepo = avatarRegions;
        _avatarCatalogRepo = avatarCatalog;
        _photoSourceResolver = photoSourceResolver;
        _avatarClassifier = avatarClassifier;
        _profileLookup = profileLookup;
        _ccipEmbedder = ccipEmbedder;
        _scopedPhotoIds = scopedPhotoIds?.ToList() ?? [];
        _pathByPhotoId = pathByPhotoId ?? new Dictionary<long, string>();
        _avatarTypeByPhotoId = avatarTypeByPhotoId ?? new Dictionary<long, string?>();
        _setSuggestionsStale = setSuggestionsStale;
        // Friends + self are cheap (a small VRCX table + two trivial lookups), so still read
        // live. The rest of what search/merge-suggestions need - the known-VRC-user cache and
        // recorded aliases - now come from OUR OWN local tables only, no VRCX gamelog query
        // here: that used to run (and refresh the cache + capture aliases) on every single Tag
        // Faces open, which a real slowness report traced to VRCX's own gamelog table having
        // no natural size bound (thousands of distinct players on a long-lived account). See
        // MainViewModel.SyncVrcPlayerDataAsync ("Sync VRC Players" button) for the explicit
        // action that actually refreshes these two caches - Tag Faces just reads whatever they
        // already have.
        _friends = profileLookup?.GetFriends() ?? [];
        _self = profileLookup?.GetSelf();
        // You're not your own VRCX friend, so the friends-list autocomplete would never
        // surface yourself - fold it into the same searchable list explicitly.
        if (_self is (string selfId, string selfName))
        {
            _friends.Insert(0, (selfId, selfName));
        }
        _knownVrcUsers = _faces.GetKnownVrcUsers();
        _aliasesByUserId = _faces.GetAllAliasesGroupedByUser();

        MergeSuggestionsList.ItemsSource = _mergeSuggestions;
        LoadPhoto(photo);
        if (suggestionsMayBeStale) _ = ShowStaleSuggestionsBannerAsync();
        // ScrollViewer gives its content infinite measure space on both axes (needed so it
        // can scroll once zoomed past the viewport), which means Stretch="Uniform" no longer
        // auto-fits the image to the window - Image just reports its native pixel size. Wait
        // for the window's first layout pass (Loaded) to know the real viewport size, then set
        // an initial zoom that reproduces the old "fit to window" starting view. Prev/Next
        // navigation (NavigateToPhoto) calls InitializeZoom directly instead - by then the
        // window's already been through its first layout pass, so there's no Loaded to wait for.
        Loaded += (_, _) => InitializeZoom();
        // Escape/right-click (below) can now close this window while the person-picker popup
        // is still open (e.g. mid-tagging a freshly-drawn manual box) - explicitly close the
        // popup first rather than trusting WPF to tear it down on its own, so
        // PersonPickerPopup_Closed's abandoned-box cleanup reliably runs through the same
        // path it always does, instead of assuming a Window closing cascades to an open
        // Popup's Closed event.
        Closing += (_, _) =>
        {
            if (PersonPickerPopup.IsOpen) PersonPickerPopup.IsOpen = false;
        };
    }

    /// <summary>Everything specific to the photo currently being tagged - shared by the
    /// constructor's initial load and NavigateToPhoto (Left/Right arrow keys - see
    /// TagFacesWindow_PreviewKeyDown), so navigating between photos in the same batch doesn't
    /// need to close and reopen this window (a real ask: doing that manually meant closing, then
    /// middle-clicking the next photo in the grid). Deliberately does NOT touch
    /// _friends/_self/_knownVrcUsers/_aliasesByUserId (loaded once in the constructor, not
    /// per-photo) or zoom (left to the caller - the constructor defers to Loaded, navigation
    /// calls InitializeZoom directly - see their respective call sites).</summary>
    private void LoadPhoto(Photo photo)
    {
        _photo = photo;
        Title = $"Tag Faces - {photo.FileName}";
        _pendingManualFaceId = null;
        _pendingManualAvatarRegionId = null;
        _renamingPersonId = null;
        _editingAliasesForUserId = null;
        if (PersonPickerPopup.IsOpen) PersonPickerPopup.IsOpen = false;
        if (AvatarPickerPopup.IsOpen) AvatarPickerPopup.IsOpen = false;
        RenameHintText.Visibility = Visibility.Collapsed;
        AliasEditorPanel.Visibility = Visibility.Collapsed;
        ClearTagButton.Visibility = Visibility.Collapsed;

        // Fire-and-forget, same convention as ShowStaleSuggestionsBannerAsync below - LoadPhoto
        // itself stays synchronous (it's called from the constructor, which can't await) while
        // the image load (which may need to download-and-cache a Discord original) happens in
        // the background. Exceptions are caught inside LoadPhotoImageAsync itself, not here -
        // an unobserved exception from a fire-and-forget Task would otherwise never surface.
        _ = LoadPhotoImageAsync(photo);

        LoadFaceData();
        // Reads _personsById, which LoadFaceData just populated - computed here (not earlier,
        // where it originally sat and silently iterated an empty dictionary every time,
        // never once surfacing a real match) deliberately after that call. Clear-and-refill the
        // existing collection (not a new instance) - MergeSuggestionsList's binding is set once,
        // outside this method, and only reacts to CollectionChanged on whatever instance it's
        // already bound to.
        _mergeSuggestions.Clear();
        foreach (var suggestion in FindManualPersonMergeSuggestions()) _mergeSuggestions.Add(suggestion);
        RedrawBoxes();
    }

    /// <summary>Resolves photo's real local path (a no-op for a local-folder photo, a download-
    /// and-cache for a not-yet-cached Discord one) and loads it into PhotoImage - split out of
    /// LoadPhoto so the resolve/download can be awaited without making LoadPhoto itself async
    /// (it's called from the constructor). Guards against a stale result: if the user has since
    /// navigated to a different photo (NavigateToPhoto) while this was still resolving/
    /// downloading, _photo will have moved on, so this no-ops instead of clobbering the newer
    /// photo's already-shown image.</summary>
    private async Task LoadPhotoImageAsync(Photo photo)
    {
        try
        {
            string localPath = await _photoSourceResolver.ResolveLocalPathAsync(photo);
            if (_photo != photo) return;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(localPath);
            bitmap.EndInit();
            bitmap.Freeze();
            PhotoImage.Source = bitmap;
        }
        catch (Exception ex)
        {
            if (_photo != photo) return;
            MessageBox.Show(this, $"Could not load the photo file:\n{ex.Message}", "Tag Faces",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Left/Right arrow keys (see TagFacesWindow_PreviewKeyDown) - offset is -1
    /// (previous) or +1 (next) within _scopedPhotoIds, the same filtered/sorted set the main
    /// grid shows. A silent no-op past either end (no wraparound), or if the target id no longer
    /// resolves to a real photo (deleted/moved out from under a stale scopedPhotoIds reference) -
    /// no visual affordance needed for a keyboard shortcut simply doing nothing at a boundary.</summary>
    private void NavigateToPhoto(int offset)
    {
        int index = _scopedPhotoIds.IndexOf(_photo.Id);
        if (index < 0) return;
        int targetIndex = index + offset;
        if (targetIndex < 0 || targetIndex >= _scopedPhotoIds.Count) return;
        if (_photos.GetById(_scopedPhotoIds[targetIndex]) is not Photo target) return;

        LoadPhoto(target);
        // Deferred, not called synchronously right here - PhotoImage.Source just changed, and
        // this gives WPF's layout system an actual pass to settle before InitializeZoom reads
        // anything - same "wait for a real layout pass" precedent as MainWindow's own
        // scroll-position restore (RestoreScrollPositionOnce) for the same class of timing
        // issue. Loaded priority runs after layout/render, ahead of user input.
        Dispatcher.BeginInvoke(InitializeZoom, DispatcherPriority.Loaded);
    }

    /// <summary>Escape closes the window outright - quicker than hunting for the X, and there's
    /// no other use for either gesture anywhere in this window (no context menus, no
    /// cancelable multi-step flow) to conflict with. Delete, while the person picker is open,
    /// is the same as clicking "Delete box" (see DeleteFaceButton_Click) - handled at the
    /// window level (not on the popup's own content) because OpenPicker never moves keyboard
    /// focus into the popup for the plain tagging flow, so a handler attached to the popup's
    /// Border would simply never see the key. Skipped while NewPersonNameTextBox has focus
    /// (typing a search/new name, renaming, or adding an alias) so Delete still does its normal
    /// "remove the next character" job there instead of unexpectedly deleting the box
    /// mid-edit.</summary>
    private void TagFacesWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (e.Key == Key.Delete && PersonPickerPopup.IsOpen && Keyboard.FocusedElement != NewPersonNameTextBox)
        {
            e.Handled = true;
            DeleteFaceButton_Click(sender, e);
        }

        // Left/Right navigate photos - takes over from WPF's default directional-navigation
        // behavior, which would otherwise move keyboard focus between this window's controls
        // (buttons, radio buttons) on these same keys. Skipped whenever a text box has focus
        // (typing in NewPersonNameTextBox or AvatarSearchTextBox) so the keys still move the
        // text cursor normally there instead of jumping to a different photo mid-type.
        if ((e.Key == Key.Left || e.Key == Key.Right) && Keyboard.FocusedElement is not TextBox)
        {
            e.Handled = true;
            NavigateToPhoto(e.Key == Key.Left ? -1 : 1);
        }
    }

    /// <summary>
    /// Right-click anywhere in the window closes it too - same rationale as Escape above, and
    /// closes on button-up rather than button-down deliberately: closing synchronously on
    /// MouseRightButtonDown destroyed this window's HWND before the matching mouse-up (which is
    /// what Windows actually shows a context menu from) had been delivered to it, so that
    /// trailing up-event fell through to whatever window was newly exposed underneath - popping
    /// the main photo grid's right-click context menu immediately after this window closed
    /// (found via a real report). Marking Down as handled (without closing yet) still suppresses
    /// any default down-triggered behavior on a child control; the close only happens once this
    /// window has fully absorbed both halves of the gesture.
    /// </summary>
    private void TagFacesWindow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void TagFacesWindow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    /// <summary>Reads the actual loaded bitmap's size (PhotoImage.Source, always available
    /// immediately after LoadPhoto - BitmapCacheOption.OnLoad makes it synchronous), not
    /// _photo.Width/Height - those come from the database's cached metadata, which is null
    /// until a library scan has processed this specific photo (a real report: navigating into a
    /// not-yet-scanned photo silently skipped fitting entirely and just kept whatever zoom the
    /// previous photo had).
    /// Uses bitmap.Width/Height (DPI-adjusted device-independent size, i.e. PixelWidth * 96 /
    /// bitmap.DpiX), NOT PixelWidth/PixelHeight (raw pixel count) - a second real report ("photo
    /// shows smaller than it should be") traced to exactly that distinction: ActualWidth here is
    /// in WPF's device-independent unit system, and dividing it by a raw pixel count silently
    /// assumes the source PNG's embedded DPI is exactly 96. If it isn't (plausible for a
    /// screenshot captured on a scaled display), the fit comes out systematically wrong by that
    /// ratio. GetImageToCanvasTransform never had this bug - it reads PhotoImage.ActualWidth,
    /// which is whatever WPF actually rendered the image at (already DPI-correct), rather than
    /// hand-computing a size from raw pixel counts - which is exactly why box positioning stayed
    /// fine while only the zoom was off. Deliberately still PixelWidth/PixelHeight over there,
    /// though - detected_faces coordinates are stored in true native-pixel space, not this
    /// device-independent one.</summary>
    private void InitializeZoom()
    {
        if (PhotoImage.Source is not BitmapImage bitmap || bitmap.Width <= 0 || bitmap.Height <= 0)
            return;
        if (ImageScrollViewer.ActualWidth == 0 || ImageScrollViewer.ActualHeight == 0)
            return;

        _fitZoomScale = Math.Min(ImageScrollViewer.ActualWidth / bitmap.Width, ImageScrollViewer.ActualHeight / bitmap.Height);
        ZoomTransform.ScaleX = _fitZoomScale;
        ZoomTransform.ScaleY = _fitZoomScale;

        // Setting ZoomTransform doesn't itself trigger a layout pass, and RedrawBoxes' offset
        // calculation is NOT actually scale-invariant the way it looks: PhotoImage (Stretch=
        // Uniform) gets centered within ImageContainer whenever the ScrollViewer decides a given
        // axis doesn't need scrolling and stretches the container to fill the viewport there -
        // which is exactly the case at low zoom (found via a live build/test loop: the box
        // offset baked in before this method ran was only ever correct by coincidence at high
        // zoom, where both axes scroll and no such centering happens - it was stale and wrong
        // everywhere else, including the initial "fit to window" view). Force a layout pass so
        // TranslatePoint reflects the real post-zoom state, then recompute box positions for it.
        ImageScrollViewer.UpdateLayout();
        RedrawBoxes();
    }

    /// <summary>
    /// Zooms toward the cursor (keeps the point under it stationary) rather than the
    /// viewport's top-left, which is the standard expectation for wheel-zoom. Clamped between
    /// the initial "fit to window" scale (can't zoom out further than the starting view) and
    /// 8x that, so the range stays consistent regardless of the source photo's resolution.
    /// </summary>
    private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_fitZoomScale <= 0) return;

        const double zoomStep = 1.15;
        double minZoom = _fitZoomScale;
        double maxZoom = _fitZoomScale * 8;

        double oldZoom = ZoomTransform.ScaleX;
        double newZoom = Math.Clamp(e.Delta > 0 ? oldZoom * zoomStep : oldZoom / zoomStep, minZoom, maxZoom);
        if (Math.Abs(newZoom - oldZoom) < 0.0001) return;

        Point cursorPos = e.GetPosition(ImageScrollViewer);
        Point contentPos = e.GetPosition(ImageContainer);

        ZoomTransform.ScaleX = newZoom;
        ZoomTransform.ScaleY = newZoom;

        ImageScrollViewer.UpdateLayout();
        ImageScrollViewer.ScrollToHorizontalOffset(contentPos.X * newZoom - cursorPos.X);
        ImageScrollViewer.ScrollToVerticalOffset(contentPos.Y * newZoom - cursorPos.Y);
        // Box offsets depend on the current zoom level (see the comment in InitializeZoom) -
        // must recompute every time zoom actually changes, not just once at window open.
        RedrawBoxes();
    }

    private void ImageScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;

        _isPanning = true;
        _panStartMousePosition = e.GetPosition(ImageScrollViewer);
        _panStartHorizontalOffset = ImageScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = ImageScrollViewer.VerticalOffset;
        ImageScrollViewer.Cursor = Cursors.ScrollAll;
        ImageScrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void ImageScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;

        Point current = e.GetPosition(ImageScrollViewer);
        ImageScrollViewer.ScrollToHorizontalOffset(_panStartHorizontalOffset - (current.X - _panStartMousePosition.X));
        ImageScrollViewer.ScrollToVerticalOffset(_panStartVerticalOffset - (current.Y - _panStartMousePosition.Y));
        e.Handled = true;
    }

    private void ImageScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        EndPanning();
    }

    /// <summary>Capture can be lost without a matching mouse-up (e.g. alt-tabbing mid-drag) -
    /// this is the only reliable place to guarantee panning state doesn't get stuck on.</summary>
    private void ImageScrollViewer_LostMouseCapture(object sender, MouseEventArgs e) => EndPanning();

    private void EndPanning()
    {
        if (!_isPanning) return;
        _isPanning = false;
        ImageScrollViewer.Cursor = Cursors.Arrow;
        ImageScrollViewer.ReleaseMouseCapture();
    }

    /// <summary>
    /// Flags a manually-created person (typed by name before their VRC identity was known)
    /// whose name matches someone VRCX already knows, so Tag Faces can offer to merge them
    /// instead of leaving two separate person rows for the same human floating around forever
    /// (found via a real report: "Lumiichu" and "Lumiichu (manual)" both showing up). Uses
    /// exact match after FuzzyNameSearch.Normalize (stylized-Unicode-tolerant, but NOT loose
    /// substring containment like the search box's own matching) deliberately - a false-
    /// positive merge suggestion accepted by the user would actually corrupt data by fusing
    /// two different people's tags together, so this only proposes matches that are safe to
    /// approve at a glance. Checked once per Tag Faces open, same cost shape as alias capture.
    /// </summary>
    private List<ManualPersonMergeSuggestion> FindManualPersonMergeSuggestions()
    {
        var candidates = _friends.Concat(_knownVrcUsers)
            .GroupBy(c => c.UserId).Select(g => g.First()).ToList();

        var suggestions = new List<ManualPersonMergeSuggestion>();
        foreach (var manual in _personsById.Values.Where(p => p.VrcUserId is null))
        {
            string normalizedManualName = FuzzyNameSearch.Normalize(manual.Name);
            var match = candidates.FirstOrDefault(c =>
                FuzzyNameSearch.Normalize(c.DisplayName).Equals(normalizedManualName, StringComparison.OrdinalIgnoreCase)
                || (_aliasesByUserId.TryGetValue(c.UserId, out var aliases)
                    && aliases.Any(a => FuzzyNameSearch.Normalize(a).Equals(normalizedManualName, StringComparison.OrdinalIgnoreCase))));
            if (match.UserId is not null)
            {
                suggestions.Add(new ManualPersonMergeSuggestion(manual.Id, manual.Name, match.UserId, match.DisplayName));
            }
        }
        return suggestions;
    }

    private void MergeSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ManualPersonMergeSuggestion suggestion) return;
        _faces.LinkManualPersonToVrcUser(suggestion.ManualPersonId, suggestion.VrcUserId);
        _mergeSuggestions.Remove(suggestion);
        LoadFaceData();
        RedrawBoxes();
    }

    private void DismissMergeSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ManualPersonMergeSuggestion suggestion) return;
        _mergeSuggestions.Remove(suggestion);
    }

    private void LoadFaceData()
    {
        _detectedFaces = _faces.GetDetectedFaces(_photo.Id);
        _labelsByFaceId = _faces.GetFaceLabelsByPhoto(_photo.Id);
        _personsById = _faces.GetAllPersons().ToDictionary(p => p.Id);
        _photoPlayers = _photos.GetPlayersForPhoto(_photo.Id);
        // Gamelog-inferred fallback only ever has rows when there's no real VRCX player data
        // for this photo (GamelogCorrelationService's scope), so only bother loading it then.
        _gamelogPlayers = _photoPlayers.Count == 0 ? _photos.GetGamelogInferredPlayersForPhoto(_photo.Id) : [];
        _avatarRegionsList = _avatarRegionRepo.GetRegionsForPhoto(_photo.Id);
        UpdateAllTaggedButtonLabel();
    }

    /// <summary>AllTaggedButton and RemoveUntaggedButton are two separate bulk actions, not one -
    /// a real report caught the original combined button counting a pending suggestion and an
    /// untagged box together as "2 faces" to confirm, when clicking it would only ever confirm
    /// one of them (the other gets deleted, not confirmed). AllTaggedButton now only ever accepts
    /// pending suggestions (has a PersonId, not yet Confirmed); RemoveUntaggedButton only ever
    /// deletes boxes with no label row at all. Both counts are raw face counts, not distinct-
    /// person counts, since the same person can legitimately appear in more than one box in the
    /// same photo (a selfie, a mirror) and each occurrence is its own remaining item.</summary>
    private void UpdateAllTaggedButtonLabel()
    {
        int suggestedCount = _detectedFaces.Count(f =>
            _labelsByFaceId.TryGetValue(f.Id, out var label) && !label.Confirmed && label.PersonId is not null);
        AllTaggedButton.Content = $"Confirm {suggestedCount} {(suggestedCount == 1 ? "face" : "faces")}";
        AllTaggedButton.IsEnabled = suggestedCount > 0;

        int untaggedCount = _detectedFaces.Count(f => !_labelsByFaceId.ContainsKey(f.Id));
        RemoveUntaggedButton.Content = $"Remove {untaggedCount} untagged {(untaggedCount == 1 ? "face" : "faces")}";
        RemoveUntaggedButton.IsEnabled = untaggedCount > 0;
    }

    private void PhotoImage_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawBoxes();

    /// <summary>
    /// Detected-face boxes are stored in the original image's pixel coordinates
    /// (Photo.Width/Height). PhotoImage.ActualWidth/Height already equals the tightly-fit
    /// (Stretch="Uniform") picture size - it does NOT stretch to fill FaceCanvas's larger
    /// cell, and is centered within it (found via a live build/test loop: boxes were
    /// rendering offset toward the top-left, since the old code re-derived letterbox
    /// centering from PhotoImage's own bounds, double-counting). So the offset is just
    /// "where does the image actually sit relative to the canvas" (via TranslatePoint), not
    /// a second round of manual centering math. Recomputed on every resize.
    /// </summary>
    private double CurrentZoomScale() => ZoomTransform.ScaleX > 0 ? ZoomTransform.ScaleX : 1.0;

    /// <summary>
    /// Where the (pre-transform) photo sits on FaceCanvas, and the native-pixel-to-canvas
    /// scale factor - shared by RedrawBoxes (drawing existing boxes) and the manual-box-draw
    /// handlers below (converting a drawn canvas rectangle back to native pixel coordinates for
    /// storage). Scale is 0 if the photo's dimensions or the image control aren't ready yet.
    /// </summary>
    private (double OffsetX, double OffsetY, double Scale) GetImageToCanvasTransform()
    {
        // Real loaded bitmap size, not _photo.Width/Height (database-cached metadata, null
        // until a library scan has processed this specific photo - see InitializeZoom's doc
        // comment for the same fix applied there).
        if (PhotoImage.Source is not BitmapImage bitmap || bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
            return (0, 0, 0);
        int imgWidth = bitmap.PixelWidth, imgHeight = bitmap.PixelHeight;
        if (PhotoImage.ActualWidth == 0 || PhotoImage.ActualHeight == 0)
            return (0, 0, 0);

        double scale = Math.Min(PhotoImage.ActualWidth / imgWidth, PhotoImage.ActualHeight / imgHeight);
        var imageOrigin = PhotoImage.TranslatePoint(new Point(0, 0), FaceCanvas);
        return (imageOrigin.X, imageOrigin.Y, scale);
    }

    /// <summary>Switching Person/Avatar mode changes which box type RedrawBoxes actually draws
    /// (see its own comments) - fires immediately on toggle so the now-irrelevant boxes vanish
    /// right away instead of lingering until some unrelated redraw.</summary>
    private void TagMode_Checked(object sender, RoutedEventArgs e) => RedrawBoxes();

    private void RedrawBoxes()
    {
        FaceCanvas.Children.Clear();
        var (offsetX, offsetY, scale) = GetImageToCanvasTransform();
        if (scale <= 0) return;

        // Box coordinates live in native-pixel/pre-transform space, then get shrunk by
        // ZoomTransform for final rendering - a fixed StrokeThickness/hit-padding written in
        // that same space would shrink right along with it, becoming sub-pixel (and getting
        // anti-aliased into a faint, near-invisible line) at low zoom. Dividing by the current
        // zoom scale here cancels that out, so the visible border is always exactly 1 real
        // screen pixel and the click padding is always exactly 5 real screen pixels,
        // regardless of zoom level.
        double zoomScale = CurrentZoomScale();
        double strokeThickness = 1.0 / zoomScale;
        double hitPadding = 5.0 / zoomScale;
        // Name labels are TextBlocks living on the same zoom-transformed FaceCanvas as the
        // boxes, so a fixed FontSize shrinks right along with the image at low zoom (a fit-to-
        // window view of a 3840x2160 photo can run zoomScale ~0.2, turning an 11px font into an
        // unreadable ~2px on screen) - same fix shape as strokeThickness/hitPadding above: divide
        // by the current zoom so the label is always exactly 13 real screen pixels regardless of
        // zoom level.
        double labelFontSize = 13.0 / zoomScale;
        double labelPaddingH = 2.0 / zoomScale;
        // Only the currently selected mode's boxes are drawn - showing both face boxes and
        // avatar regions at once cluttered the view and made it easy to misclick, since a face
        // is typically positioned inside (or very near) the avatar region around the same
        // person. An existing box's own click handler still opens its picker regardless of
        // mode when it IS drawn (see the avatar-region loop's own comment) - this only changes
        // which set is visible/hit-testable at all, not that per-box behavior.
        if (PersonModeRadio.IsChecked == true)
        {
        foreach (var face in _detectedFaces)
        {
            _labelsByFaceId.TryGetValue(face.Id, out var label);
            bool confirmed = label is not null && label.Confirmed && label.PersonId is not null;
            bool suggested = label is not null && !label.Confirmed
                && (label.Source == FaceLabelSource.EmbeddingMatch || label.Source == FaceLabelSource.ExifElimination)
                && label.PersonId is not null;
            // High-confidence combined-score suggestion (avatar/co-occurrence boosts pushed it
            // past FaceMatcher.AutoTagThreshold) - still Confirmed=false like `suggested` above
            // (a human must still open this photo before it's treated as reviewed or feeds
            // future centroids/boosts), but shown with more visual confidence than a guess.
            bool autoTagged = label is not null && !label.Confirmed
                && label.Source == FaceLabelSource.AutoTagged && label.PersonId is not null;
            // Confirmed=true with PersonId=null is the "<unknown>" case (a deliberately marked
            // false-positive detection) - distinct from having no FaceLabel row at all (never
            // reviewed), which is what the default yellow/untagged state below still means.
            bool markedNotAFace = label is not null && label.Confirmed && label.PersonId is null;

            string? personName = null;
            Brush boxColor = Brushes.Yellow;
            if (confirmed && _personsById.TryGetValue(label!.PersonId!.Value, out var confirmedPerson))
            {
                personName = confirmedPerson.Name;
                boxColor = Brushes.LimeGreen;
            }
            else if (suggested && _personsById.TryGetValue(label!.PersonId!.Value, out var suggestedPerson))
            {
                // No "?" prefix - the orange box color already says "this is a suggestion, not
                // confirmed" on its own; the number is FaceMatcher's own raw match score (NOT a
                // percentage - same "F2 decimal" convention as MinSuggestionConfidenceLabel in
                // MainWindow's filter bar), so a real user can gauge relative strength between
                // suggestions without a misleading/inaccurate "% confidence" implication.
                personName = $"{suggestedPerson.Name} ({label.Confidence:F2})";
                boxColor = Brushes.Orange;
            }
            else if (autoTagged && _personsById.TryGetValue(label!.PersonId!.Value, out var autoTaggedPerson))
            {
                personName = $"{autoTaggedPerson.Name} ({label.Confidence:F2})";
                boxColor = Brushes.DeepSkyBlue;
            }
            else if (markedNotAFace)
            {
                personName = "<unknown>";
                boxColor = Brushes.Gray;
            }

            double left = offsetX + face.X * scale;
            double top = offsetY + face.Y * scale;
            double width = face.Width * scale;
            double height = face.Height * scale;

            // A Rectangle with no Fill only hit-tests its Stroke line, not its interior -
            // clicking anywhere inside a box wouldn't register at all. This invisible
            // rectangle is the actual click target: Fill=Transparent makes the whole area
            // hit-testable, and it's padded 5px beyond the visual border on every side so
            // clicking near - but not exactly on - the edge (inward or outward) still counts.
            // Added first/below the visual border, which has IsHitTestVisible="False" so this
            // is always what actually receives the click regardless of paint order.
            var hitTarget = new Rectangle
            {
                Width = width + hitPadding * 2,
                Height = height + hitPadding * 2,
                Fill = Brushes.Transparent,
                Tag = face.Id,
                Cursor = Cursors.Hand,
            };
            hitTarget.MouseLeftButtonUp += FaceBox_MouseLeftButtonUp;
            Canvas.SetLeft(hitTarget, left - hitPadding);
            Canvas.SetTop(hitTarget, top - hitPadding);
            FaceCanvas.Children.Add(hitTarget);

            var visualBorder = new Rectangle
            {
                Width = width,
                Height = height,
                Stroke = boxColor,
                StrokeThickness = strokeThickness,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(visualBorder, left);
            Canvas.SetTop(visualBorder, top);
            FaceCanvas.Children.Add(visualBorder);

            if (personName is not null)
            {
                var nameLabel = new TextBlock
                {
                    Text = personName,
                    Background = boxColor,
                    Foreground = Brushes.Black,
                    FontSize = labelFontSize,
                    Padding = new Thickness(labelPaddingH, 0, labelPaddingH, 0),
                };
                Canvas.SetLeft(nameLabel, left);
                Canvas.SetTop(nameLabel, top + height);
                FaceCanvas.Children.Add(nameLabel);
            }
        }
        }

        // Avatar regions - same hit-target/visual-border/name-label shape as the face loop
        // above, just their own colors (magenta/yellow/orange, distinct from every face-box
        // color already in use) and their own click handler (AvatarBox_MouseLeftButtonUp). Drawn
        // only in Avatar mode, same "only the selected mode's boxes are visible" reasoning as
        // the face loop above - switch back to Person mode to review/correct a face box while
        // this photo also has avatar regions.
        if (AvatarModeRadio.IsChecked == true)
        {
        foreach (var region in _avatarRegionsList)
        {
            // Orange (same visual language as an unconfirmed face suggestion) for an
            // auto-detected region (AvatarBodyDetectionService) still awaiting review -
            // Confirmed is always true for a manual region (see AvatarRegion.Confirmed's doc
            // comment), so this branch only ever applies to the auto-detected case.
            Brush regionColor = !region.Confirmed ? Brushes.Orange
                : region.AvatarCatalogId is not null ? Brushes.Magenta : Brushes.Yellow;

            double left = offsetX + region.X * scale;
            double top = offsetY + region.Y * scale;
            double width = region.Width * scale;
            double height = region.Height * scale;

            var hitTarget = new Rectangle
            {
                Width = width + hitPadding * 2,
                Height = height + hitPadding * 2,
                Fill = Brushes.Transparent,
                // Negated: DetectedFace.Id and AvatarRegion.Id are independent auto-increment
                // sequences that can (and do) collide in value - both kinds of hit-target
                // Rectangle share this same FaceCanvas.Children collection tagged with a bare
                // long, so a same-valued face/region pair would be ambiguous to a Tag-based
                // lookup otherwise. SQLite autoincrement ids are always positive, so negative
                // unambiguously means "this Tag is an AvatarRegion.Id" - see AvatarBox_
                // MouseLeftButtonUp and the post-creation lookup in FaceCanvas_MouseLeftButtonUp
                // for the matching negation back.
                Tag = -region.Id,
                Cursor = Cursors.Hand,
            };
            hitTarget.MouseLeftButtonUp += AvatarBox_MouseLeftButtonUp;
            Canvas.SetLeft(hitTarget, left - hitPadding);
            Canvas.SetTop(hitTarget, top - hitPadding);
            FaceCanvas.Children.Add(hitTarget);

            var visualBorder = new Rectangle
            {
                Width = width,
                Height = height,
                Stroke = regionColor,
                StrokeThickness = strokeThickness,
                StrokeDashArray = [4, 2], // dashed - visually distinct from solid face boxes at a glance
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(visualBorder, left);
            Canvas.SetTop(visualBorder, top);
            FaceCanvas.Children.Add(visualBorder);

            if (region.AvatarDisplayName is not null)
            {
                // Confidence suffix only for a still-unconfirmed (auto-detected) region - same
                // "(0.XX)" convention as a face suggestion's confidence display; a confirmed
                // region (manual, or already reviewed) just shows the plain name.
                string labelText = !region.Confirmed && region.Confidence is float confidence
                    ? $"{region.AvatarDisplayName} ({confidence:F2})"
                    : region.AvatarDisplayName;
                var nameLabel = new TextBlock
                {
                    Text = labelText,
                    Background = regionColor,
                    Foreground = Brushes.Black,
                    FontSize = labelFontSize,
                    Padding = new Thickness(labelPaddingH, 0, labelPaddingH, 0),
                };
                Canvas.SetLeft(nameLabel, left);
                Canvas.SetTop(nameLabel, top + height);
                FaceCanvas.Children.Add(nameLabel);
            }
        }
        }
    }

    private void FaceBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var box = (Rectangle)sender;
        OpenPicker((long)box.Tag, box);
    }

    private void AvatarBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var box = (Rectangle)sender;
        OpenAvatarPicker(-(long)box.Tag, box); // see RedrawBoxes' avatar-region loop for why Tag is negated
    }

    /// <summary>
    /// Click-drag on empty canvas draws a new face box for a face the detector missed - left
    /// button down starts it, but only when the click didn't land on an existing face's
    /// hitTarget rectangle (identified by having a long Tag), so clicking an existing box still
    /// goes through FaceBox_MouseLeftButtonUp exactly as before.
    /// </summary>
    private void FaceCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Rectangle { Tag: long }) return;

        _isDrawingBox = true;
        _drawStartCanvasPoint = e.GetPosition(FaceCanvas);
        _drawPreviewRect = new Rectangle
        {
            // Width/Height default to NaN until MouseMove sets them - a plain click (no
            // intervening MouseMove) would leave them NaN, and "NaN < minScreenPixels" is
            // FALSE under IEEE754, silently defeating the minimum-size guard below. Starting
            // at a real 0 makes a plain click correctly measure as a zero-size box.
            Width = 0,
            Height = 0,
            Stroke = AvatarModeRadio.IsChecked == true ? Brushes.Magenta : Brushes.Cyan,
            StrokeThickness = 1.0 / CurrentZoomScale(),
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(_drawPreviewRect, _drawStartCanvasPoint.X);
        Canvas.SetTop(_drawPreviewRect, _drawStartCanvasPoint.Y);
        FaceCanvas.Children.Add(_drawPreviewRect);
        FaceCanvas.CaptureMouse();
    }

    private void FaceCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDrawingBox || _drawPreviewRect is null) return;

        Point current = e.GetPosition(FaceCanvas);
        Canvas.SetLeft(_drawPreviewRect, Math.Min(current.X, _drawStartCanvasPoint.X));
        Canvas.SetTop(_drawPreviewRect, Math.Min(current.Y, _drawStartCanvasPoint.Y));
        _drawPreviewRect.Width = Math.Abs(current.X - _drawStartCanvasPoint.X);
        _drawPreviewRect.Height = Math.Abs(current.Y - _drawStartCanvasPoint.Y);
    }

    private void FaceCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDrawingBox) return;
        _isDrawingBox = false;
        FaceCanvas.ReleaseMouseCapture();

        Rectangle? preview = _drawPreviewRect;
        _drawPreviewRect = null;
        if (preview is null) return;
        FaceCanvas.Children.Remove(preview);

        double canvasLeft = Canvas.GetLeft(preview);
        double canvasTop = Canvas.GetTop(preview);
        double canvasWidth = preview.Width;
        double canvasHeight = preview.Height;

        // Require a deliberate drag (8 real screen pixels in each dimension), not an accidental
        // click on empty canvas - same screen-pixel-independent-of-zoom idea as hitPadding.
        double zoomScale = CurrentZoomScale();
        const double minScreenPixels = 8;
        if (canvasWidth * zoomScale < minScreenPixels || canvasHeight * zoomScale < minScreenPixels) return;

        var (offsetX, offsetY, scale) = GetImageToCanvasTransform();
        if (scale <= 0) return;
        if (PhotoImage.Source is not BitmapImage bitmap) return;
        int imgWidth = bitmap.PixelWidth, imgHeight = bitmap.PixelHeight;

        int x = (int)Math.Clamp((canvasLeft - offsetX) / scale, 0, imgWidth);
        int y = (int)Math.Clamp((canvasTop - offsetY) / scale, 0, imgHeight);
        int width = (int)Math.Clamp(canvasWidth / scale, 1, imgWidth - x);
        int height = (int)Math.Clamp(canvasHeight / scale, 1, imgHeight - y);

        if (AvatarModeRadio.IsChecked == true)
        {
            var newRegion = _avatarRegionRepo.AddRegion(_photo.Id, x, y, width, height);
            LoadFaceData();
            RedrawBoxes();

            if (FaceCanvas.Children.OfType<Rectangle>().FirstOrDefault(r => r.Tag is long id && id == -newRegion.Id) is Rectangle newRegionTarget)
            {
                _pendingManualAvatarRegionId = newRegion.Id;
                OpenAvatarPicker(newRegion.Id, newRegionTarget);
            }
            return;
        }

        var newFace = _faces.AddManualFace(_photo.Id, new FaceBox(x, y, width, height));
        LoadFaceData();
        RedrawBoxes();

        if (FaceCanvas.Children.OfType<Rectangle>().FirstOrDefault(r => r.Tag is long id && id == newFace.Id) is Rectangle newHitTarget)
        {
            _pendingManualFaceId = newFace.Id;
            OpenPicker(newFace.Id, newHitTarget);
        }
    }

    /// <summary>See _pendingManualFaceId - fires whenever the popup closes for any reason
    /// (outside click, Escape, an action that explicitly closes it), so this is the one place
    /// that reliably catches "user backed out without tagging the box they just drew".</summary>
    private void PersonPickerPopup_Closed(object? sender, EventArgs e)
    {
        // The picker is per-photo, so caching notes/bios across different photos' pickers isn't
        // wanted - only across keystrokes within one open popup (see _noteBioCache).
        _noteBioCache.Clear();

        if (_pendingManualFaceId is not long pendingId) return;
        _pendingManualFaceId = null;
        _faces.DeleteDetectedFace(pendingId);
        LoadFaceData();
        RedrawBoxes();
    }

    /// <summary>Same "backed out without tagging" cleanup as PersonPickerPopup_Closed, for a
    /// freshly-drawn avatar region instead of a face box.</summary>
    private void AvatarPickerPopup_Closed(object? sender, EventArgs e)
    {
        if (_pendingManualAvatarRegionId is not long pendingId) return;
        _pendingManualAvatarRegionId = null;
        _avatarRegionRepo.DeleteRegion(pendingId);
        LoadFaceData();
        RedrawBoxes();
    }

    private void OpenAvatarPicker(long regionId, Rectangle box)
    {
        _activeAvatarRegionId = regionId;
        var region = _avatarRegionsList.FirstOrDefault(r => r.Id == regionId);
        ClearAvatarTagButton.Visibility = region?.AvatarCatalogId is not null ? Visibility.Visible : Visibility.Collapsed;
        ConfirmAvatarRegionButton.Visibility = region is { Confirmed: false, AvatarCatalogId: not null }
            ? Visibility.Visible : Visibility.Collapsed;

        AvatarSearchTextBox.Text = "";
        AvatarSearchListBox.ItemsSource = SearchAvatarEntries("");
        EditCatalogInfoButton.IsEnabled = false;

        AvatarPickerPopup.PlacementTarget = box;
        AvatarPickerPopup.IsOpen = true;
        AvatarSearchTextBox.Focus();
    }

    /// <summary>Substring match against the classifier's full known-avatar list (see
    /// AvatarTypeService.AllEntries) - simpler than the person picker's FuzzyNameSearch/alias
    /// matching, since avatar names don't have the "stylized Unicode display name" problem VRC
    /// usernames do. Empty query returns everything, same "browse the full list" convention as
    /// SearchPlayerFilterOptions.</summary>
    private List<(string Label, string? CatalogId)> SearchAvatarEntries(string query)
    {
        var all = _avatarClassifier?.AllEntries ?? [];
        if (string.IsNullOrWhiteSpace(query)) return all.ToList();
        return all.Where(e => e.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void AvatarSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        AvatarSearchListBox.ItemsSource = SearchAvatarEntries(AvatarSearchTextBox.Text);
    }

    private void AvatarSearchListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EditCatalogInfoButton.IsEnabled = AvatarSearchListBox.SelectedItem is ValueTuple<string, string?>;
    }

    /// <summary>Resolves the selected entry to its AvatarCatalog row (auto-creating one on first
    /// use, same as an actual pick would - see AvatarCatalogRepository.
    /// GetOrCreateByTrainedCatalogId) and opens AvatarCatalogEditWindow for it. Doesn't tag the
    /// region - editing catalog info and picking an avatar for this region are independent
    /// actions.</summary>
    private void EditCatalogInfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (AvatarSearchListBox.SelectedItem is not ValueTuple<string, string?> selected) return;
        string label = selected.Item1;
        string? catalogId = selected.Item2;
        if (catalogId is null) return; // no confident-match entries never appear in this list, but guard anyway

        long resolvedCatalogId = _avatarCatalogRepo.GetOrCreateByTrainedCatalogId(catalogId, label);
        new AvatarCatalogEditWindow(_avatarCatalogRepo, resolvedCatalogId).ShowDialog();
    }

    private void AvatarSearchListBox_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (AvatarSearchListBox.SelectedItem is not ValueTuple<string, string?> selected) return;
        string label = selected.Item1;
        string? catalogId = selected.Item2;
        long? resolvedCatalogId = catalogId is not null
            ? _avatarCatalogRepo.GetOrCreateByTrainedCatalogId(catalogId, label)
            : null;
        _pendingManualAvatarRegionId = null; // resolved - not a backed-out draw
        _avatarRegionRepo.SetRegionTag(_activeAvatarRegionId, resolvedCatalogId, label);
        AvatarPickerPopup.IsOpen = false;
        LoadFaceData();
        RedrawBoxes();
    }

    private void ConfirmAvatarRegionButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingManualAvatarRegionId = null;
        _avatarRegionRepo.ConfirmRegion(_activeAvatarRegionId);
        AvatarPickerPopup.IsOpen = false;
        LoadFaceData();
        RedrawBoxes();
    }

    private void ClearAvatarTagButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingManualAvatarRegionId = null;
        _avatarRegionRepo.SetRegionTag(_activeAvatarRegionId, null, null);
        AvatarPickerPopup.IsOpen = false;
        LoadFaceData();
        RedrawBoxes();
    }

    private void DeleteAvatarRegionButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingManualAvatarRegionId = null; // explicit delete, not the auto-cleanup path
        _avatarRegionRepo.DeleteRegion(_activeAvatarRegionId);
        AvatarPickerPopup.IsOpen = false;
        LoadFaceData();
        RedrawBoxes();
    }

    /// <summary>VRCX embeds `"id": ""` (empty string, not a missing/null field) for a player it
    /// couldn't resolve a real user id for (common for hidden/private accounts) - see
    /// PngMetadataReader.VrcxPhotoAuthor, whose Id is `required string`, so PhotoPlayer.UserId
    /// carries that empty string through verbatim rather than null. A real, reproduced bug: every
    /// unresolved player in the SAME photo shares that identical empty string, so treating it as
    /// a normal VrcUserId made FindOrCreatePersonByVrcUserId find-or-merge unrelated people who
    /// each just happened to be unresolved (tagging "PersonA (in this instance, per VRCX)" could
    /// silently reuse whichever OTHER unresolved person was tagged first, anywhere, ever), and
    /// separately made PickerItem.AlreadyConfirmedInPhoto bold every unresolved player's name the
    /// moment any ONE of them got confirmed. Every PickerItem built from a PhotoPlayer/
    /// GamelogInferredPlayer must route its UserId through this first.</summary>
    private static string? NormalizeVrcUserId(string userId) => string.IsNullOrEmpty(userId) ? null : userId;

    /// <summary>Real (non-self) VRCX friends get a People-icon glyph, self gets a Contact-icon
    /// glyph, wherever a PickerItem shows up - both the static default list (OpenPicker) and the
    /// type-to-search results (NewPersonNameTextBox_TextChanged) route through this one method,
    /// so the two lists can't drift out of sync on this again (an earlier version only wired the
    /// glyph into the search-results path, leaving the default list - what actually shows before
    /// you type anything - always blank, including the local account's own self entry).
    /// Kept out of DisplayText (a plain string) rather than embedded in it: a PUA glyph mixed into
    /// a larger run of ordinary text did not reliably paint even with an explicit
    /// "Segoe UI, Segoe MDL2 Assets" font-fallback list (confirmed by a real report - the glyph
    /// was invisible, no fallback occurred) - Segoe UI evidently reports a present-but-blank
    /// glyph across that PUA range, so WPF's glyph-presence-based fallback never triggers. A
    /// dedicated TextBlock with FontFamily set directly (see TagFacesWindow.xaml's FriendGlyph
    /// column) is the same pattern this window's own alias/rename icon buttons already use
    /// successfully, so this returns just the raw glyph character for that separate element.</summary>
    private string? FriendGlyphFor(string? userId)
    {
        if (userId is null) return null;
        if (userId == _self?.UserId) return "";
        return _friends.Any(f => f.UserId == userId) ? "" : null;
    }

    /// <summary>Best-effort match score between the face currently being tagged and a
    /// present-in-instance candidate, used only to rank OpenPicker's "in this instance" section
    /// (score only makes sense against a prior of "this person was actually there" - sorting the
    /// much wider type-to-search results the same way would rank total strangers alongside real
    /// candidates). This mirrors FaceSuggestionService's own trimmed-references/centroid-fallback
    /// scoring (see FaceMatcher.GetTrimmedReferences), minus its VrcProfileThumbnail fallback and
    /// batching - this runs live against a handful of candidates each time the picker opens, not
    /// the whole library, so there's no need for either. Null whenever a score can't be computed
    /// (the active face has no stored embedding yet - Suggest Faces/Detect Faces hasn't run -,
    /// the candidate has no linked VRC account, or that VRC account has never been registered or
    /// has zero confirmed reference photos yet) - those candidates keep their original
    /// (alphabetical) position rather than being sorted to a misleading spot.</summary>
    private float? ScoreCandidate(string? vrcUserId, float[]? activeEmbedding)
    {
        if (vrcUserId is null || activeEmbedding is null) return null;
        if (_personsById.Values.FirstOrDefault(p => p.VrcUserId == vrcUserId) is not RegisteredPerson person) return null;

        var refs = _faces.GetReferenceEmbeddingsForPerson(person.Id)
            .Select(CcipEmbeddingService.BytesToEmbedding).ToList();
        if (FaceMatcher.GetTrimmedReferences(refs) is not List<float[]> trimmed) return null;

        List<float[]> candidates = trimmed.Count >= FaceMatcher.MinReferencesForTrimming
            ? trimmed
            : FaceMatcher.ComputeCentroid(trimmed) is float[] centroid ? [centroid] : [];
        return candidates.Count > 0 ? candidates.Max(r => FaceMatcher.CosineSimilarity(activeEmbedding, r)) : null;
    }

    private void OpenPicker(long detectedFaceId, Rectangle box)
    {
        _activeFaceId = detectedFaceId;
        _renamingPersonId = null;
        _editingAliasesForUserId = null;
        RenameHintText.Visibility = Visibility.Collapsed;
        AliasEditorPanel.Visibility = Visibility.Collapsed;
        _labelsByFaceId.TryGetValue(detectedFaceId, out var existing);
        bool alreadyTagged = existing is not null && existing.Confirmed && existing.PersonId is not null;
        ClearTagButton.Visibility = alreadyTagged ? Visibility.Visible : Visibility.Collapsed;

        var items = new List<PickerItem>();

        bool isSuggestion = existing is not null && !existing.Confirmed
            && (existing.Source == FaceLabelSource.EmbeddingMatch || existing.Source == FaceLabelSource.ExifElimination)
            && existing.PersonId is not null;
        if (isSuggestion && _personsById.TryGetValue(existing!.PersonId!.Value, out var suggestedPerson))
        {
            items.Add(new PickerItem($"Confirm: {suggestedPerson.Name}", suggestedPerson.VrcUserId, suggestedPerson.Id, IsConfirmSuggestion: true));
        }

        // isSuggestion and isAutoTagged are mutually exclusive - a face has exactly one
        // FaceLabel row with exactly one Source, so at most one of these two picker entries is
        // ever added.
        bool isAutoTagged = existing is not null && !existing.Confirmed
            && existing.Source == FaceLabelSource.AutoTagged && existing.PersonId is not null;
        if (isAutoTagged && _personsById.TryGetValue(existing!.PersonId!.Value, out var autoTaggedPerson))
        {
            items.Add(new PickerItem($"Confirm: {autoTaggedPerson.Name}", autoTaggedPerson.VrcUserId, autoTaggedPerson.Id, IsConfirmAutoTag: true));
        }

        items.Add(new PickerItem("<unknown> (clear tag)", null, null, IsNotAFace: true));

        // Ranked by match score against the face being tagged where one's available (see
        // ScoreCandidate) - a present-in-instance candidate is exactly the pool where a score is
        // meaningful (a real prior of "this person was actually here"), unlike the much wider
        // type-to-search results below. Unscored candidates (no embedding yet, never registered,
        // no reference photos) keep their original position at the back, stable-sorted.
        float[]? activeEmbedding = _detectedFaces.FirstOrDefault(f => f.Id == detectedFaceId)?.Embedding is byte[] embeddingBytes
            ? CcipEmbeddingService.BytesToEmbedding(embeddingBytes)
            : null;
        var presentItems = new List<(PickerItem Item, float? Score)>();
        foreach (var player in _photoPlayers)
        {
            string? userId = NormalizeVrcUserId(player.UserId);
            float? score = ScoreCandidate(userId, activeEmbedding);
            string label = $"{player.DisplayName} (in this instance, per VRCX)" + (score is float s ? $" ({s:F2})" : "");
            presentItems.Add((new PickerItem(label, userId, null, RawName: player.DisplayName, FriendGlyph: FriendGlyphFor(userId)), score));
        }
        // Gamelog-inferred fallback (GamelogCorrelationService) - only ever populated when
        // _photoPlayers is empty, so there's no overlap/duplication risk between the two loops.
        foreach (var player in _gamelogPlayers)
        {
            string? userId = NormalizeVrcUserId(player.UserId);
            float? score = ScoreCandidate(userId, activeEmbedding);
            string label = $"{player.DisplayName} (in this instance, per log)" + (score is float s ? $" ({s:F2})" : "");
            presentItems.Add((new PickerItem(label, userId, null, RawName: player.DisplayName, FriendGlyph: FriendGlyphFor(userId)), score));
        }
        items.AddRange(presentItems.OrderByDescending(x => x.Score ?? float.NegativeInfinity).Select(x => x.Item));
        // Recently-tagged shortlist, not every registered person ever created - that list only
        // grows and became unusable (type-to-search below covers the rest; see
        // NewPersonNameTextBox_TextChanged). Capped at 10 by GetRecentlyTaggedPersons.
        foreach (var person in _faces.GetRecentlyTaggedPersons())
        {
            // person.VrcUserId is null for a manually-created person (no linked VRC account).
            // PhotoPlayer.UserId for an unresolved player is "" (VRCX's own sentinel, not null -
            // see NormalizeVrcUserId), so `p.UserId == person.VrcUserId` naturally stays false
            // whenever person.VrcUserId is null - "" never equals null in C#. An earlier version
            // of this guard assumed both sides went null and compared them as if that were the
            // same "already listed above" match, which silently hid EVERY manual person whenever
            // the photo had any unresolved player - confirmed live: this created two separate
            // "Lumiichu" person records because the first was invisible when the second was
            // typed. The explicit `is not null` below is what actually prevents that, regardless
            // of which sentinel PhotoPlayer.UserId turns out to use.
            if (person.VrcUserId is not null && (_photoPlayers.Any(p => p.UserId == person.VrcUserId)
                || _gamelogPlayers.Any(p => p.UserId == person.VrcUserId))) continue;
            items.Add(new PickerItem(person.Name, person.VrcUserId, person.Id, FriendGlyph: FriendGlyphFor(person.VrcUserId)));
        }

        items = WithNoteTooltips(items);
        _staticPickerItems = items;
        SuggestionListBox.ItemsSource = items;
        NewPersonNameTextBox.Text = "";
        PersonPickerPopup.PlacementTarget = box;
        PersonPickerPopup.IsOpen = true;
    }

    private void SuggestionListBox_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (SuggestionListBox.SelectedItem is not PickerItem item) return;
        // Every branch below is a deliberate choice, not backing out - must clear this BEFORE
        // closing the popup, since setting IsOpen=false fires PersonPickerPopup_Closed
        // synchronously (clearing it inside ApplyTag etc. would run too late).
        _pendingManualFaceId = null;
        PersonPickerPopup.IsOpen = false;

        if (item.IsConfirmSuggestion)
        {
            // Preserves the actual stored source (EmbeddingMatch or ExifElimination) rather than
            // hardcoding EmbeddingMatch, so confirming an elimination-derived suggestion keeps
            // that provenance instead of silently relabeling it as a CCIP match.
            var confirmSource = _labelsByFaceId.TryGetValue(_activeFaceId, out var currentLabel)
                ? currentLabel.Source : FaceLabelSource.EmbeddingMatch;
            ApplyTag(_personsById[item.ExistingPersonId!.Value], confirmSource);
            _faces.ResolveSuggestionLog(_activeFaceId, SuggestionOutcome.ConfirmedAsIs);
            return;
        }

        if (item.IsConfirmAutoTag)
        {
            ApplyTag(_personsById[item.ExistingPersonId!.Value], FaceLabelSource.AutoTagged);
            _faces.ResolveSuggestionLog(_activeFaceId, SuggestionOutcome.ConfirmedAsIs);
            return;
        }

        if (item.IsNotAFace)
        {
            // Clears back to untagged (yellow) rather than confirming a dedicated "gray -
            // definitely not a face" state, per a real report: that gray dead-end wasn't useful
            // and just meant an extra "Clear tag" click later to undo. Same effect as
            // ClearTagButton_Click, just reachable here too (that button only shows once a face
            // is already confirmed to a person - this list item is always present).
            _faces.DeleteFaceLabel(_activeFaceId);
            _faces.ResolveSuggestionLog(_activeFaceId, SuggestionOutcome.Ignored);
            LoadFaceData();
            RedrawBoxes();
            return;
        }

        RegisteredPerson person = item.ExistingPersonId is long existingId
            ? _personsById[existingId]
            : item.VrcUserId is string vrcUserId
                ? _faces.FindOrCreatePersonByVrcUserId(vrcUserId, item.EffectiveName)
                : _faces.CreatePerson(item.EffectiveName);

        bool isNewVrcLink = item.ExistingPersonId is null && item.VrcUserId is not null;
        _labelsByFaceId.TryGetValue(_activeFaceId, out var previousLabel);
        bool pickedSameSuggestedPerson = previousLabel is not null && !previousLabel.Confirmed && previousLabel.PersonId == person.Id;
        ApplyTag(person);
        // Safe to call unconditionally even when this face never had a pending suggestion
        // (e.g. a plain untagged box tagged directly) - ResolveSuggestionLog no-ops in that
        // case. Picking the SAME person the suggestion already named (e.g. via the search box
        // instead of the pinned "Confirm: {name}" entry) confirms it, not corrects it.
        _faces.ResolveSuggestionLog(_activeFaceId,
            pickedSameSuggestedPerson ? SuggestionOutcome.ConfirmedAsIs : SuggestionOutcome.CorrectedToDifferentPerson);

        if (isNewVrcLink && _profileLookup is not null)
        {
            long personId = person.Id;
            string userId = item.VrcUserId!;
            var faces = _faces;
            var lookup = _profileLookup;
            _ = Task.Run(async () =>
            {
                byte[]? thumb = await lookup.TryFetchLatestThumbnailAsync(userId);
                if (thumb is not null) faces.SetVrcProfileThumbnail(personId, thumb);
            });
        }
    }

    private void NewPersonNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        string name = NewPersonNameTextBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        if (_editingAliasesForUserId is string aliasUserId)
        {
            // Adding an alias doesn't resolve the active face's tag or close the popup - the
            // editor panel stays open so multiple aliases can be added in a row.
            _faces.AddAlias(aliasUserId, name);
            _aliasesByUserId = _faces.GetAllAliasesGroupedByUser();
            NewPersonNameTextBox.Text = "";
            RefreshAliasEditorList(aliasUserId);
            return;
        }

        if (_renamingPersonId is long personId)
        {
            // Renaming an unrelated person doesn't resolve the active face's tag - if this
            // popup belongs to a just-drawn manual box, leave _pendingManualFaceId set so
            // PersonPickerPopup_Closed still cleans it up if the user closes without tagging it.
            PersonPickerPopup.IsOpen = false;
            _faces.RenamePerson(personId, name);
            _renamingPersonId = null;
            LoadFaceData();
            RedrawBoxes();
            return;
        }

        // Creating + tagging a new person IS a deliberate choice - must clear before closing
        // the popup, since IsOpen=false fires PersonPickerPopup_Closed synchronously.
        _pendingManualFaceId = null;
        PersonPickerPopup.IsOpen = false;
        ApplyTag(_faces.CreatePerson(name));
        // A brand-new person can never be the person a pending suggestion named, so this is
        // always a correction (or a harmless no-op via ResolveSuggestionLog's Pending-only
        // WHERE clause if this face had no pending suggestion at all).
        _faces.ResolveSuggestionLog(_activeFaceId, SuggestionOutcome.CorrectedToDifferentPerson);
    }

    /// <summary>
    /// Typing 2+ characters searches three sources instead of showing the static suggestion
    /// list: every already-registered person (so re-selecting someone you've typed before -
    /// manual or VRCX-linked - reuses them via ExistingPersonId instead of risking a duplicate;
    /// this is what replaced dumping every registered person into the static list, which only
    /// grows), VRCX's live friends list (VrcxProfileLookupService.GetFriends, cheap enough to
    /// query on every open), and the local known-VRC-user cache - everyone VRCX has EVER
    /// reported (friends or gamelog) as of the last "Sync VRC Players" run, not just current
    /// friends (found via a real report: a real person had a resolved id in the gamelog but was
    /// never a friend, so friends-only search never found them). That cache is local-only, not
    /// a live gamelog query - see MainViewModel.SyncVrcPlayerDataAsync for why (a live gamelog
    /// scan on every Tag Faces open got slow once this account's gamelog history grew large),
    /// so it can lag behind VRCX until the next sync. Each source is skipped for a user id
    /// already covered by an earlier one, so nobody appears twice. Matching goes through
    /// FuzzyNameSearch, not a plain Contains, so VRCX "fancy text" stylized names (small caps,
    /// fullwidth, Cyrillic/Greek Latin-lookalikes) match what a human reads them as, not their
    /// literal codepoints (also a real report - a friend's display name used Unicode small
    /// capitals). Picking any match goes through the normal path in SuggestionListBox_MouseUp.
    /// Clearing the search restores the original static list.
    /// </summary>
    private void NewPersonNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_renamingPersonId is not null) return; // renaming types the exact existing name
        if (_editingAliasesForUserId is not null) return; // alias entry types a new alias, not a search

        string query = NewPersonNameTextBox.Text.Trim();
        if (query.Length < 2)
        {
            SuggestionListBox.ItemsSource = _staticPickerItems;
            return;
        }

        // Checks the primary name first; only falls through to aliases (see VrcUserAlias) if
        // that didn't match. AliasesToShow is non-null only when an alias is what matched, per
        // the "only show the alias list in parens when the alias is what matched" call - a
        // match on the primary name doesn't need explaining via aliases.
        (bool Matches, List<string>? AliasesToShow) EvaluateMatch(string name, string? userId)
        {
            if (FuzzyNameSearch.Matches(name, query)) return (true, null);
            if (userId is not null && _aliasesByUserId.TryGetValue(userId, out var aliases)
                && aliases.Any(a => FuzzyNameSearch.Matches(a, query)))
            {
                return (true, aliases);
            }
            return (false, null);
        }

        string BuildLabel(string name, List<string>? aliasesToShow, string? sourceSuffix)
        {
            string aliasPart = aliasesToShow is { Count: > 0 } ? $" ({string.Join(", ", aliasesToShow)})" : "";
            string sourcePart = sourceSuffix is not null ? $" ({sourceSuffix})" : "";
            return $"{name}{aliasPart}{sourcePart}";
        }

        var registeredVrcUserIds = _personsById.Values
            .Where(p => p.VrcUserId is not null)
            .Select(p => p.VrcUserId!)
            .ToHashSet();

        var personMatches = _personsById.Values
            .Select(p => (Person: p, Eval: EvaluateMatch(p.Name, p.VrcUserId)))
            .Where(x => x.Eval.Matches)
            .OrderBy(x => x.Person.Name)
            .Select(x => new PickerItem(
                BuildLabel(x.Person.Name, x.Eval.AliasesToShow, null),
                x.Person.VrcUserId, x.Person.Id, RawName: x.Person.Name, FriendGlyph: FriendGlyphFor(x.Person.VrcUserId)));

        var friendMatches = _friends
            .Where(f => !registeredVrcUserIds.Contains(f.UserId))
            .Select(f => (Friend: f, Eval: EvaluateMatch(f.DisplayName, f.UserId)))
            .Where(x => x.Eval.Matches)
            .Select(x => new PickerItem(
                BuildLabel(x.Friend.DisplayName, x.Eval.AliasesToShow, null),
                x.Friend.UserId, null, RawName: x.Friend.DisplayName, FriendGlyph: FriendGlyphFor(x.Friend.UserId)));

        // Everyone VRCX has ever reported (friends or gamelog) as of the last "Sync VRC
        // Players" run (MainViewModel.SyncVrcPlayerDataAsync) - a local-only read, not a live
        // VRCX query, so this can lag behind reality until the next sync (see KnownVrcUser).
        var friendIds = _friends.Select(f => f.UserId).ToHashSet();
        var cachedMatches = _knownVrcUsers
            .Where(u => !registeredVrcUserIds.Contains(u.UserId) && !friendIds.Contains(u.UserId))
            .Select(u => (User: u, Eval: EvaluateMatch(u.DisplayName, u.UserId)))
            .Where(x => x.Eval.Matches)
            .Select(x => new PickerItem(
                BuildLabel(x.User.DisplayName, x.Eval.AliasesToShow, "previously seen"), x.User.UserId, null, RawName: x.User.DisplayName));

        var matches = WithNoteTooltips(personMatches.Concat(friendMatches).Concat(cachedMatches).Take(20).ToList());
        SuggestionListBox.ItemsSource = matches.Count > 0 ? matches : _staticPickerItems;
    }

    private void RenamePersonButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not PickerItem item || item.ExistingPersonId is not long personId) return;

        _renamingPersonId = personId;
        NewPersonNameTextBox.Text = item.EffectiveName;
        NewPersonNameTextBox.Focus();
        NewPersonNameTextBox.SelectAll();
        RenameHintText.Text = $"Renaming \"{item.EffectiveName}\" - press Enter to save";
        RenameHintText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Switches the shared NewPersonNameTextBox into "add alias" mode (per the approved design:
    /// extend the existing rename-pencil popup rather than build a separate dialog) - Enter in
    /// NewPersonNameTextBox_KeyDown then adds an alias instead of renaming/creating a person
    /// while _editingAliasesForUserId is set. Aliases are keyed by the raw VRC user_id, so this
    /// button is only ever visible (CanEditAliases) for a picker row that actually has one.
    /// </summary>
    private void AddAliasButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not PickerItem item || item.VrcUserId is not string userId) return;

        _renamingPersonId = null;
        RenameHintText.Visibility = Visibility.Collapsed;
        _editingAliasesForUserId = userId;
        AliasEditorHeaderText.Text = $"Aliases for \"{item.EffectiveName}\" - type a previous name and press Enter";
        RefreshAliasEditorList(userId);
        AliasEditorPanel.Visibility = Visibility.Visible;
        NewPersonNameTextBox.Text = "";
        NewPersonNameTextBox.Focus();
    }

    private void RemoveAliasButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_editingAliasesForUserId is not string userId) return;
        if ((sender as FrameworkElement)?.DataContext is not VrcUserAlias alias) return;

        _faces.RemoveAlias(alias.Id);
        _aliasesByUserId = _faces.GetAllAliasesGroupedByUser();
        RefreshAliasEditorList(userId);
    }

    private void AliasEditorDoneButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _editingAliasesForUserId = null;
        AliasEditorPanel.Visibility = Visibility.Collapsed;
        NewPersonNameTextBox.Text = "";
        NewPersonNameTextBox.Focus();
    }

    private void RefreshAliasEditorList(string userId)
    {
        var aliases = _faces.GetAliasesForUser(userId);
        AliasListBox.ItemsSource = aliases;
        NoAliasesText.Visibility = aliases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearTagButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingManualFaceId = null;
        PersonPickerPopup.IsOpen = false;
        _faces.DeleteFaceLabel(_activeFaceId);
        LoadFaceData();
        RedrawBoxes();
    }

    private void DeleteFaceButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingManualFaceId = null; // explicit delete, not the auto-cleanup path
        PersonPickerPopup.IsOpen = false;
        _faces.ResolveSuggestionLog(_activeFaceId, SuggestionOutcome.Ignored);
        _faces.DeleteDetectedFace(_activeFaceId);
        LoadFaceData();
        RedrawBoxes();
    }

    /// <summary>Bulk-accepts every remaining pending suggestion (orange - a real FaceLabel row,
    /// PersonId set, just not Confirmed yet), same as picking "Confirm: {name}" per-box
    /// (SuggestionListBox_MouseUp's IsConfirmSuggestion/IsConfirmAutoTag branches). Leaves
    /// untagged (yellow, no label row at all) boxes untouched - see RemoveUntaggedButton_Click
    /// for those; a real report caught these two being combined into one button and one count,
    /// where "Confirm N faces" silently included boxes the click would only ever delete, not
    /// confirm.</summary>
    private void AllTaggedButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingManualFaceId = null;
        PersonPickerPopup.IsOpen = false;
        bool confirmedAny = false;
        foreach (var face in _detectedFaces)
        {
            if (!_labelsByFaceId.TryGetValue(face.Id, out var label)) continue;
            if (label.Confirmed || label.PersonId is null) continue;
            _faces.UpsertFaceLabel(face.Id, label.PersonId.Value, confirmed: true, label.Source);
            _faces.ResolveSuggestionLog(face.Id, SuggestionOutcome.ConfirmedAsIs);
            confirmedAny = true;
        }
        if (confirmedAny) MarkSuggestionsStale();
        LoadFaceData();
        RedrawBoxes();
    }

    /// <summary>Bulk cleanup for once you've reviewed everyone recognizable in this photo: every
    /// remaining untagged (yellow) box - no FaceLabel row at all, i.e. never reviewed, not the
    /// same as the gray "confirmed as &lt;unknown&gt;" state - is a bad detection by elimination,
    /// same as clicking Delete box on each one individually. Leaves pending suggestions (orange)
    /// untouched - see AllTaggedButton_Click for those.</summary>
    private void RemoveUntaggedButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingManualFaceId = null;
        PersonPickerPopup.IsOpen = false;
        foreach (var face in _detectedFaces)
        {
            if (_labelsByFaceId.ContainsKey(face.Id)) continue;
            _faces.ResolveSuggestionLog(face.Id, SuggestionOutcome.Ignored);
            _faces.DeleteDetectedFace(face.Id);
        }
        LoadFaceData();
        RedrawBoxes();
    }

    private void ApplyTag(RegisteredPerson person, FaceLabelSource source = FaceLabelSource.Manual)
    {
        _faces.UpsertFaceLabel(_activeFaceId, person.Id, confirmed: true, source);
        MarkSuggestionsStale();
        LoadFaceData();
        RedrawBoxes();
    }

    /// <summary>A confirm just happened, so whoever it was for may now have different (or
    /// their first-ever) reference embeddings - other faces already in view could newly deserve
    /// a suggestion for them without waiting for a full library-wide Suggest Faces run. Shows
    /// this window's own banner immediately, and tells the caller (MainWindow, via
    /// _setSuggestionsStale) so a DIFFERENT Tag Faces window opened later for another photo -
    /// this window is a singleton, fully re-created each time - still knows to offer it too, even
    /// if this window gets closed without anyone clicking "Refresh now". A no-op when the caller
    /// didn't wire the feature up at all (ccipEmbedder/scopedPhotoIds never provided - see the
    /// constructor).</summary>
    private void MarkSuggestionsStale()
    {
        if (_ccipEmbedder is null || _scopedPhotoIds.Count == 0) return;
        _setSuggestionsStale?.Invoke(true);
        _ = ShowStaleSuggestionsBannerAsync();
    }

    /// <summary>Rough, deliberately-labeled-as-approximate estimate, not a measurement - actual
    /// per-face cost varies with hardware (DirectML vs CPU execution provider) and image size.
    /// Embedding a not-yet-seen face needs a full image decode + CCIP backbone forward pass;
    /// scoring an already-embedded face only needs CCIP's much smaller metric-head model, so the
    /// two get very different weights.</summary>
    private const double EstimatedSecondsPerEmbed = 0.1;
    private const double EstimatedSecondsPerScore = 0.02;

    /// <summary>Bumped by every method that overwrites StaleSuggestionsText for its own reason
    /// (this one, a refresh starting/finishing, dismiss) - a background estimate that finishes
    /// after something newer has already taken over just checks its own stamp and gives up
    /// instead of clobbering that newer text. Matters because rapid-fire tagging can start
    /// several of these estimate calculations before the first one's DB scan even returns.</summary>
    private int _staleBannerGeneration;

    /// <summary>The two DB scans behind the estimate below used to run synchronously on the UI
    /// thread on every single confirm - a real report ("matching faces makes the app not
    /// responding") traced to exactly this: with no active filter, scopedPhotoIds is the WHOLE
    /// library, and EF Core's translated `WHERE photo_id IN (...)` over thousands of ids is not
    /// free. The banner now appears instantly with a placeholder, and the real counts (hence the
    /// estimate) fill in once the backgrounded scan actually returns.</summary>
    private async Task ShowStaleSuggestionsBannerAsync()
    {
        int generation = ++_staleBannerGeneration;
        StaleSuggestionsText.Text = $"Someone was just tagged - checking how much there is to refresh for the {_scopedPhotoIds.Count} photos in view...";
        RefreshSuggestionsButton.IsEnabled = false;
        StaleSuggestionsBanner.Visibility = Visibility.Visible;
        RedrawBoxes(); // banner appearing changed Row 0's height - reposition the face boxes now, not on whatever unrelated event happens to trigger it next

        var counts = await Task.Run(() => (
            Embed: _faces.GetDetectedFacesWithoutEmbedding(_scopedPhotoIds).Count,
            Score: _faces.GetFacesNeedingSuggestion(_scopedPhotoIds).Count));
        if (generation != _staleBannerGeneration) return; // superseded by a newer confirm/action meanwhile

        double estimatedSeconds = counts.Embed * EstimatedSecondsPerEmbed + counts.Score * EstimatedSecondsPerScore;
        StaleSuggestionsText.Text = counts.Embed + counts.Score == 0
            ? "Someone was just tagged - suggestions for the photos in view are already up to date."
            : $"Someone was just tagged - refresh suggestions for the {_scopedPhotoIds.Count} photos currently in view? (~{Math.Max(1, (int)Math.Ceiling(estimatedSeconds))}s estimated)";
        RefreshSuggestionsButton.IsEnabled = counts.Embed + counts.Score > 0;
    }

    private async void RefreshSuggestionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingSuggestions || _ccipEmbedder is null) return;
        _staleBannerGeneration++; // invalidate any estimate scan still in flight
        _isRefreshingSuggestions = true;
        RefreshSuggestionsButton.IsEnabled = false;
        PhotoRepository? eliminationRepo = _photos.GetBoolSetting(SettingsKeys.EnableExifElimination, true) ? _photos : null;
        var result = await FaceSuggestionService.RunAsync(
            _faces, _ccipEmbedder, _pathByPhotoId, _avatarTypeByPhotoId,
            msg => StaleSuggestionsText.Text = msg, _scopedPhotoIds, eliminationRepo);
        _isRefreshingSuggestions = false;
        _setSuggestionsStale?.Invoke(false);
        // Left visible (dismiss it explicitly, or it's replaced next time MarkSuggestionsStale
        // fires) showing what the refresh actually found, rather than silently vanishing -
        // "0 new suggestions" is a legitimate, useful answer to see, not just a success/failure.
        string exifPart = result.ExifEliminations > 0 ? $" ({result.ExifEliminations} by VRCX-presence elimination)" : "";
        StaleSuggestionsText.Text = $"Suggestions refreshed: {result.Suggested} new{exifPart} across the {_scopedPhotoIds.Count} photos in view.";
        RefreshSuggestionsButton.IsEnabled = false;
        // The current photo is itself one of the scoped photos - a face here could have just
        // picked up a fresh suggestion (or lost a stale one, see RunAsync's !accept branch).
        LoadFaceData();
        RedrawBoxes();
    }

    private void DismissStaleSuggestionsBanner_Click(object sender, RoutedEventArgs e)
    {
        _staleBannerGeneration++; // invalidate any estimate scan still in flight
        StaleSuggestionsBanner.Visibility = Visibility.Collapsed;
        RedrawBoxes(); // banner disappearing changed Row 0's height back - reposition the face boxes now
    }
}

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using VrcPhotoManager.Data;
using VrcPhotoManager.Models;
using VrcPhotoManager.Services;

namespace VrcPhotoManager.Views;

public partial class TagFacesWindow : Window
{
    private readonly FaceRepository _faces;
    private readonly PhotoRepository _photos;
    private readonly VrcxProfileLookupService? _profileLookup;
    private readonly Photo _photo;

    private List<DetectedFace> _detectedFaces = [];
    private Dictionary<long, FaceLabel> _labelsByFaceId = [];
    private Dictionary<long, RegisteredPerson> _personsById = [];
    private List<PhotoPlayer> _photoPlayers = [];
    private List<GamelogInferredPlayer> _gamelogPlayers = [];
    private List<(string UserId, string DisplayName)> _friends = [];
    private List<(string UserId, string DisplayName)> _gamelogSeenPlayers = [];
    private List<(string UserId, string DisplayName)> _knownVrcUsers = [];
    private Dictionary<string, List<string>> _aliasesByUserId = [];
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

    /// <summary>
    /// RawName carries the actual primary name separate from DisplayText, which can have any
    /// number of parenthetical decorations appended ("(VRCX friend)", an alias list, etc.) -
    /// trying to recover the real name by string-Replace-ing every known suffix off DisplayText
    /// got fragile fast (5+ Replace calls, one per suffix format) and would only get worse once
    /// the alias list's content varies per item. EffectiveName is what callers should actually
    /// use; RawName is null (falls back to DisplayText, which IS the raw name) for items that
    /// were never given a suffix in the first place.
    /// </summary>
    private record PickerItem(string DisplayText, string? VrcUserId, long? ExistingPersonId, bool IsConfirmSuggestion = false, bool IsNotAFace = false, string? RawName = null)
    {
        public string EffectiveName => RawName ?? DisplayText;

        /// <summary>Rename (pencil) button only makes sense for an already-registered person
        /// with no linked VRC account - not the "confirm suggestion"/"&lt;unknown&gt;" pseudo-
        /// entries, not a bare VRCX player/friend row that hasn't been linked to a
        /// RegisteredPerson yet, and not a person who already has a known VRC username (their
        /// name comes from VRCX, so editing it here would just drift out of sync).</summary>
        public bool CanRename => ExistingPersonId is not null && VrcUserId is null && !IsConfirmSuggestion && !IsNotAFace;

        /// <summary>The "+" alias button needs a real VRC user id to key aliases off of -
        /// available much more broadly than CanRename (any friend/gamelog/cached/registered
        /// entry with a VrcUserId, not just already-registered manual people).</summary>
        public bool CanEditAliases => VrcUserId is not null && !IsConfirmSuggestion && !IsNotAFace;
    }

    public TagFacesWindow(FaceRepository faces, PhotoRepository photos, VrcxProfileLookupService? profileLookup, Photo photo)
    {
        InitializeComponent();
        DialogWindowBehavior.HideMinimizeAndMaximizeButtons(this);
        // The person-picker Popup is a transparent, separately-hwnd'd child window - it taking
        // keyboard focus (e.g. typing a new name) can itself trigger Deactivated on this window
        // even though the user never clicked away, so skip the close while it's open.
        DialogWindowBehavior.CloseOnDeactivated(this, stillOpenGuard: () => PersonPickerPopup.IsOpen);
        DialogWindowBehavior.OpenNearCursor(this);
        _faces = faces;
        _photos = photos;
        _profileLookup = profileLookup;
        _photo = photo;
        Title = $"Tag Faces - {photo.FileName}";
        _friends = profileLookup?.GetFriends() ?? [];
        _gamelogSeenPlayers = profileLookup?.GetGamelogSeenPlayers() ?? [];
        _self = profileLookup?.GetSelf();
        // You're not your own VRCX friend, so the friends-list autocomplete would never
        // surface yourself - fold it into the same searchable list explicitly.
        if (_self is (string selfId, string selfName))
        {
            _friends.Insert(0, (selfId, selfName));
        }
        // Refresh the permanent local cache with whatever VRCX returned this session (see
        // KnownVrcUser), then load it for search - a fallback for anyone only findable via
        // VRCX data that's since gone away (gamelog cleared, friend removed).
        _faces.UpsertKnownVrcUsers(_friends.Concat(_gamelogSeenPlayers));
        _knownVrcUsers = _faces.GetKnownVrcUsers();

        // Automatic alias capture (see VrcUserAlias) - real rename history is a genuine
        // supplement, but checked live it's NOT a substitute for manual entry: a real example
        // had zero rename history anywhere in local VRCX data, since the renames predated
        // VRCX ever observing that account. Filters out whatever's already the current/latest
        // name for that user, so a person's own current name never shows up as its own alias.
        if (_profileLookup is not null)
        {
            var currentNames = _friends.Concat(_gamelogSeenPlayers).Concat(_knownVrcUsers)
                .GroupBy(p => p.UserId)
                .ToDictionary(g => g.Key, g => g.First().DisplayName);
            var historyCandidates = _profileLookup.GetFriendRenameHistory()
                .Concat(_profileLookup.GetGamelogNameHistory())
                .Where(c => !currentNames.TryGetValue(c.UserId, out var current)
                    || !string.Equals(current, c.Alias, StringComparison.Ordinal));
            _faces.CaptureAliasesFromHistory(historyCandidates);
        }
        _aliasesByUserId = _faces.GetAllAliasesGroupedByUser();

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(photo.LocalPath);
            bitmap.EndInit();
            bitmap.Freeze();
            PhotoImage.Source = bitmap;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load the photo file:\n{ex.Message}", "Tag Faces",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        LoadFaceData();
        RedrawBoxes();
        // ScrollViewer gives its content infinite measure space on both axes (needed so it
        // can scroll once zoomed past the viewport), which means Stretch="Uniform" no longer
        // auto-fits the image to the window - Image just reports its native pixel size. Wait
        // for the window's first layout pass (Loaded) to know the real viewport size, then set
        // an initial zoom that reproduces the old "fit to window" starting view.
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

    /// <summary>Escape closes the window outright - quicker than hunting for the X, and there's
    /// no other use for either gesture anywhere in this window (no context menus, no
    /// cancelable multi-step flow) to conflict with.</summary>
    private void TagFacesWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
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

    private void InitializeZoom()
    {
        if (_photo.Width is not int imgWidth || _photo.Height is not int imgHeight || imgWidth == 0 || imgHeight == 0)
            return;
        if (ImageScrollViewer.ActualWidth == 0 || ImageScrollViewer.ActualHeight == 0)
            return;

        _fitZoomScale = Math.Min(ImageScrollViewer.ActualWidth / imgWidth, ImageScrollViewer.ActualHeight / imgHeight);
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

    private void LoadFaceData()
    {
        _detectedFaces = _faces.GetDetectedFaces(_photo.Id);
        _labelsByFaceId = _faces.GetFaceLabelsByPhoto(_photo.Id);
        _personsById = _faces.GetAllPersons().ToDictionary(p => p.Id);
        _photoPlayers = _photos.GetPlayersForPhoto(_photo.Id);
        // Gamelog-inferred fallback only ever has rows when there's no real VRCX player data
        // for this photo (GamelogCorrelationService's scope), so only bother loading it then.
        _gamelogPlayers = _photoPlayers.Count == 0 ? _photos.GetGamelogInferredPlayersForPhoto(_photo.Id) : [];
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
        if (_photo.Width is not int imgWidth || _photo.Height is not int imgHeight || imgWidth == 0 || imgHeight == 0)
            return (0, 0, 0);
        if (PhotoImage.ActualWidth == 0 || PhotoImage.ActualHeight == 0)
            return (0, 0, 0);

        double scale = Math.Min(PhotoImage.ActualWidth / imgWidth, PhotoImage.ActualHeight / imgHeight);
        var imageOrigin = PhotoImage.TranslatePoint(new Point(0, 0), FaceCanvas);
        return (imageOrigin.X, imageOrigin.Y, scale);
    }

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
        foreach (var face in _detectedFaces)
        {
            _labelsByFaceId.TryGetValue(face.Id, out var label);
            bool confirmed = label is not null && label.Confirmed && label.PersonId is not null;
            bool suggested = label is not null && !label.Confirmed
                && label.Source == FaceLabelSource.EmbeddingMatch && label.PersonId is not null;
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
                personName = $"? {suggestedPerson.Name}";
                boxColor = Brushes.Orange;
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

    private void FaceBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var box = (Rectangle)sender;
        OpenPicker((long)box.Tag, box);
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
            Stroke = Brushes.Cyan,
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
        if (_photo.Width is not int imgWidth || _photo.Height is not int imgHeight) return;

        int x = (int)Math.Clamp((canvasLeft - offsetX) / scale, 0, imgWidth);
        int y = (int)Math.Clamp((canvasTop - offsetY) / scale, 0, imgHeight);
        int width = (int)Math.Clamp(canvasWidth / scale, 1, imgWidth - x);
        int height = (int)Math.Clamp(canvasHeight / scale, 1, imgHeight - y);

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
        if (_pendingManualFaceId is not long pendingId) return;
        _pendingManualFaceId = null;
        _faces.DeleteDetectedFace(pendingId);
        LoadFaceData();
        RedrawBoxes();
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
            && existing.Source == FaceLabelSource.EmbeddingMatch && existing.PersonId is not null;
        if (isSuggestion && _personsById.TryGetValue(existing!.PersonId!.Value, out var suggestedPerson))
        {
            items.Add(new PickerItem($"Confirm: {suggestedPerson.Name}", suggestedPerson.VrcUserId, suggestedPerson.Id, IsConfirmSuggestion: true));
        }

        items.Add(new PickerItem("<unknown> (wrongly detected face)", null, null, IsNotAFace: true));

        foreach (var player in _photoPlayers)
        {
            items.Add(new PickerItem($"{player.DisplayName} (in this instance, per VRCX)", player.UserId, null, RawName: player.DisplayName));
        }
        // Gamelog-inferred fallback (GamelogCorrelationService) - only ever populated when
        // _photoPlayers is empty, so there's no overlap/duplication risk between the two loops.
        foreach (var player in _gamelogPlayers)
        {
            items.Add(new PickerItem($"{player.DisplayName} (in this instance, per log)", player.UserId, null, RawName: player.DisplayName));
        }
        // Recently-tagged shortlist, not every registered person ever created - that list only
        // grows and became unusable (type-to-search below covers the rest; see
        // NewPersonNameTextBox_TextChanged). Capped at 10 by GetRecentlyTaggedPersons.
        foreach (var person in _faces.GetRecentlyTaggedPersons())
        {
            // person.VrcUserId is null for a manually-created person (no linked VRC account) -
            // and so is PhotoPlayer.UserId for a player VRCX couldn't resolve an id for (common
            // for hidden/private users). Comparing those two nulls as if they were the same
            // "already listed above" match silently hid EVERY manual person whenever the photo
            // had any unresolved player - confirmed live: this created two separate "Lumiichu"
            // person records because the first was invisible when the second was typed. Only
            // treat it as a real duplicate when both sides have an actual, non-null id.
            if (person.VrcUserId is not null && (_photoPlayers.Any(p => p.UserId == person.VrcUserId)
                || _gamelogPlayers.Any(p => p.UserId == person.VrcUserId))) continue;
            items.Add(new PickerItem(person.Name, person.VrcUserId, person.Id));
        }

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
            ApplyTag(_personsById[item.ExistingPersonId!.Value], FaceLabelSource.EmbeddingMatch);
            return;
        }

        if (item.IsNotAFace)
        {
            _faces.UpsertFaceLabel(_activeFaceId, null, confirmed: true, FaceLabelSource.Manual);
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
        ApplyTag(person);

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
    }

    /// <summary>
    /// Typing 2+ characters searches three sources instead of showing the static suggestion
    /// list: every already-registered person (so re-selecting someone you've typed before -
    /// manual or VRCX-linked - reuses them via ExistingPersonId instead of risking a duplicate;
    /// this is what replaced dumping every registered person into the static list, which only
    /// grows), VRCX's friends list (VrcxProfileLookupService.GetFriends), and everyone VRCX's
    /// gamelog has ever recorded a resolved id for - not just friends (found via a real report:
    /// a real person had a resolved id in the gamelog but was never a friend, so friends-only
    /// search never found them). Each source is skipped for a user id already covered by an
    /// earlier one, so nobody appears twice. Matching goes through FuzzyNameSearch, not a plain
    /// Contains, so VRCX "fancy text" stylized names (small caps, fullwidth, Cyrillic/Greek
    /// Latin-lookalikes) match what a human reads them as, not their literal codepoints (also a
    /// real report - a friend's display name used Unicode small capitals). Picking any match
    /// goes through the normal path in SuggestionListBox_MouseUp. Clearing the search restores
    /// the original static list.
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
                BuildLabel(x.Person.Name, x.Eval.AliasesToShow, null), x.Person.VrcUserId, x.Person.Id, RawName: x.Person.Name));

        var friendMatches = _friends
            .Where(f => !registeredVrcUserIds.Contains(f.UserId))
            .Select(f => (Friend: f, Eval: EvaluateMatch(f.DisplayName, f.UserId)))
            .Where(x => x.Eval.Matches)
            .Select(x => new PickerItem(
                BuildLabel(x.Friend.DisplayName, x.Eval.AliasesToShow, x.Friend.UserId == _self?.UserId ? "you" : "VRCX friend"),
                x.Friend.UserId, null, RawName: x.Friend.DisplayName));

        var friendIds = _friends.Select(f => f.UserId).ToHashSet();
        var gamelogMatches = _gamelogSeenPlayers
            .Where(p => !registeredVrcUserIds.Contains(p.UserId) && !friendIds.Contains(p.UserId))
            .Select(p => (Player: p, Eval: EvaluateMatch(p.DisplayName, p.UserId)))
            .Where(x => x.Eval.Matches)
            .Select(x => new PickerItem(
                BuildLabel(x.Player.DisplayName, x.Eval.AliasesToShow, "seen in VRCX"), x.Player.UserId, null, RawName: x.Player.DisplayName));

        // Fallback of last resort: the local KnownVrcUser cache, for anyone only findable via
        // VRCX data that's since gone away (gamelog cleared, friend removed). Already covered
        // by one of the live sources above whenever VRCX still has the data, so this only ever
        // surfaces someone the OTHER three missed.
        var gamelogIds = _gamelogSeenPlayers.Select(p => p.UserId).ToHashSet();
        var cachedMatches = _knownVrcUsers
            .Where(u => !registeredVrcUserIds.Contains(u.UserId) && !friendIds.Contains(u.UserId) && !gamelogIds.Contains(u.UserId))
            .Select(u => (User: u, Eval: EvaluateMatch(u.DisplayName, u.UserId)))
            .Where(x => x.Eval.Matches)
            .Select(x => new PickerItem(
                BuildLabel(x.User.DisplayName, x.Eval.AliasesToShow, "previously seen"), x.User.UserId, null, RawName: x.User.DisplayName));

        var matches = personMatches.Concat(friendMatches).Concat(gamelogMatches).Concat(cachedMatches).Take(20).ToList();
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
        _faces.DeleteDetectedFace(_activeFaceId);
        LoadFaceData();
        RedrawBoxes();
    }

    private void ApplyTag(RegisteredPerson person, FaceLabelSource source = FaceLabelSource.Manual)
    {
        _faces.UpsertFaceLabel(_activeFaceId, person.Id, confirmed: true, source);
        LoadFaceData();
        RedrawBoxes();
    }
}

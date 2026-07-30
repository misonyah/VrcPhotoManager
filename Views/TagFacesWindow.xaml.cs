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
    private long _activeFaceId;
    private double _fitZoomScale = 1.0;

    private bool _isPanning;
    private Point _panStartMousePosition;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;

    private record PickerItem(string DisplayText, string? VrcUserId, long? ExistingPersonId, bool IsConfirmSuggestion = false);

    public TagFacesWindow(FaceRepository faces, PhotoRepository photos, VrcxProfileLookupService? profileLookup, Photo photo)
    {
        InitializeComponent();
        _faces = faces;
        _photos = photos;
        _profileLookup = profileLookup;
        _photo = photo;
        Title = $"Tag Faces - {photo.FileName}";

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
    private void RedrawBoxes()
    {
        FaceCanvas.Children.Clear();
        if (_photo.Width is not int imgWidth || _photo.Height is not int imgHeight || imgWidth == 0 || imgHeight == 0)
            return;
        if (PhotoImage.ActualWidth == 0 || PhotoImage.ActualHeight == 0)
            return;

        double scale = Math.Min(PhotoImage.ActualWidth / imgWidth, PhotoImage.ActualHeight / imgHeight);
        var imageOrigin = PhotoImage.TranslatePoint(new System.Windows.Point(0, 0), FaceCanvas);
        double offsetX = imageOrigin.X;
        double offsetY = imageOrigin.Y;

        // Box coordinates live in native-pixel/pre-transform space, then get shrunk by
        // ZoomTransform for final rendering - a fixed StrokeThickness/hit-padding written in
        // that same space would shrink right along with it, becoming sub-pixel (and getting
        // anti-aliased into a faint, near-invisible line) at low zoom. Dividing by the current
        // zoom scale here cancels that out, so the visible border is always exactly 1 real
        // screen pixel and the click padding is always exactly 5 real screen pixels,
        // regardless of zoom level.
        double zoomScale = ZoomTransform.ScaleX > 0 ? ZoomTransform.ScaleX : 1.0;
        double strokeThickness = 1.0 / zoomScale;
        double hitPadding = 5.0 / zoomScale;
        foreach (var face in _detectedFaces)
        {
            _labelsByFaceId.TryGetValue(face.Id, out var label);
            bool confirmed = label is not null && label.Confirmed && label.PersonId is not null;
            bool suggested = label is not null && !label.Confirmed
                && label.Source == FaceLabelSource.EmbeddingMatch && label.PersonId is not null;

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
                    FontSize = 11,
                    Padding = new Thickness(2, 0, 2, 0),
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

    private void OpenPicker(long detectedFaceId, Rectangle box)
    {
        _activeFaceId = detectedFaceId;
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

        foreach (var player in _photoPlayers)
        {
            items.Add(new PickerItem($"{player.DisplayName} (in this photo)", player.UserId, null));
        }
        foreach (var person in _personsById.Values.OrderBy(p => p.Name))
        {
            if (_photoPlayers.Any(p => p.UserId == person.VrcUserId)) continue; // already listed above
            items.Add(new PickerItem(person.Name, person.VrcUserId, person.Id));
        }

        SuggestionListBox.ItemsSource = items;
        NewPersonNameTextBox.Text = "";
        PersonPickerPopup.PlacementTarget = box;
        PersonPickerPopup.IsOpen = true;
    }

    private void SuggestionListBox_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (SuggestionListBox.SelectedItem is not PickerItem item) return;
        PersonPickerPopup.IsOpen = false;

        if (item.IsConfirmSuggestion)
        {
            ApplyTag(_personsById[item.ExistingPersonId!.Value], FaceLabelSource.EmbeddingMatch);
            return;
        }

        RegisteredPerson person = item.ExistingPersonId is long existingId
            ? _personsById[existingId]
            : item.VrcUserId is string vrcUserId
                ? _faces.FindOrCreatePersonByVrcUserId(vrcUserId, item.DisplayText.Replace(" (in this photo)", ""))
                : _faces.CreatePerson(item.DisplayText);

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

        PersonPickerPopup.IsOpen = false;
        ApplyTag(_faces.CreatePerson(name));
    }

    private void IgnoreButton_Click(object sender, RoutedEventArgs e)
    {
        PersonPickerPopup.IsOpen = false;
        _faces.UpsertFaceLabel(_activeFaceId, null, confirmed: true, FaceLabelSource.Manual);
        LoadFaceData();
        RedrawBoxes();
    }

    private void ClearTagButton_Click(object sender, RoutedEventArgs e)
    {
        PersonPickerPopup.IsOpen = false;
        _faces.DeleteFaceLabel(_activeFaceId);
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

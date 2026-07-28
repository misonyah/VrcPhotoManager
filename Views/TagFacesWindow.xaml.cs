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

    private record PickerItem(string DisplayText, string? VrcUserId, long? ExistingPersonId);

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
    /// (Photo.Width/Height), but Stretch="Uniform" letterboxes the displayed Image inside its
    /// layout slot - boxes must be scaled and offset to match, recomputed on every resize.
    /// </summary>
    private void RedrawBoxes()
    {
        FaceCanvas.Children.Clear();
        if (_photo.Width is not int imgWidth || _photo.Height is not int imgHeight || imgWidth == 0 || imgHeight == 0)
            return;
        if (PhotoImage.ActualWidth == 0 || PhotoImage.ActualHeight == 0)
            return;

        // PhotoImage.ActualWidth/Height already equals the tightly-fit (Stretch="Uniform")
        // picture size - it does NOT stretch to fill FaceCanvas's larger cell, and is
        // centered within it. So the letterbox math is just "where does the image actually
        // sit relative to the canvas", not a second round of centering math against
        // PhotoImage's own bounds (that double-counts and shifts boxes toward the top-left).
        double scale = Math.Min(PhotoImage.ActualWidth / imgWidth, PhotoImage.ActualHeight / imgHeight);
        var imageOrigin = PhotoImage.TranslatePoint(new System.Windows.Point(0, 0), FaceCanvas);
        double offsetX = imageOrigin.X;
        double offsetY = imageOrigin.Y;

        foreach (var face in _detectedFaces)
        {
            bool tagged = _labelsByFaceId.TryGetValue(face.Id, out var label) && label.Confirmed && label.PersonId is not null;
            string? personName = tagged && _personsById.TryGetValue(label!.PersonId!.Value, out var person) ? person.Name : null;

            var rect = new Rectangle
            {
                Width = face.Width * scale,
                Height = face.Height * scale,
                Stroke = tagged ? Brushes.LimeGreen : Brushes.Yellow,
                StrokeThickness = 2,
                Tag = face.Id,
                Cursor = Cursors.Hand,
            };
            rect.MouseLeftButtonUp += FaceBox_MouseLeftButtonUp;
            Canvas.SetLeft(rect, offsetX + face.X * scale);
            Canvas.SetTop(rect, offsetY + face.Y * scale);
            FaceCanvas.Children.Add(rect);

            if (personName is not null)
            {
                var nameLabel = new TextBlock
                {
                    Text = personName,
                    Background = Brushes.LimeGreen,
                    Foreground = Brushes.Black,
                    FontSize = 11,
                    Padding = new Thickness(2, 0, 2, 0),
                };
                Canvas.SetLeft(nameLabel, offsetX + face.X * scale);
                Canvas.SetTop(nameLabel, offsetY + face.Y * scale + face.Height * scale);
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
        bool alreadyTagged = _labelsByFaceId.TryGetValue(detectedFaceId, out var existing)
            && existing.Confirmed && existing.PersonId is not null;
        ClearTagButton.Visibility = alreadyTagged ? Visibility.Visible : Visibility.Collapsed;

        var items = new List<PickerItem>();
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

    private void ApplyTag(RegisteredPerson person)
    {
        _faces.UpsertFaceLabel(_activeFaceId, person.Id, confirmed: true, FaceLabelSource.Manual);
        LoadFaceData();
        RedrawBoxes();
    }
}

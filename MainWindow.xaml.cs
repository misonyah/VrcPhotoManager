using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VrcPhotoManager.ViewModels;

namespace VrcPhotoManager;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _hoverTimer;
    private FrameworkElement? _hoverTarget;
    private Views.TagFacesWindow? _openTagFacesWindow;

    /// <summary>The photo the preview overlay is currently showing, if any - MouseMove (not
    /// just MouseEnter) restarts the hover-debounce timer so the preview keeps tracking small
    /// cursor movements within the same thumbnail, which means the timer can tick again for a
    /// photo it's already showing. Without this check, that replayed the whole grow-in
    /// animation from scratch on every tick even though nothing actually changed (found via a
    /// real report of the animation restarting mid-hover).</summary>
    private PhotoViewModel? _currentPreviewPhoto;

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainViewModel();
        DataContext = viewModel;
        SetPlayerFilterText(TextBoxTextFor(viewModel.SelectedPlayerFilter));

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.25) };
        _hoverTimer.Tick += HoverTimer_Tick;

        Closing += (_, _) => viewModel.RequestShutdown();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        HidePreviewOverlay();
        new Views.AboutWindow().Show();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        HidePreviewOverlay();
        new Views.SettingsWindow(vm.Repo).Show();
    }

    private void ViewMetadata_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not PhotoViewModel photo) return;
        if (DataContext is not MainViewModel vm) return;
        HidePreviewOverlay();
        new Views.MetadataWindow(photo, vm.Repo).Show();
    }

    private void CopyVrcdnUrl_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not PhotoViewModel photo) return;
        if (DataContext is not MainViewModel vm) return;

        if (photo.RemoteUrl is string url)
        {
            Clipboard.SetText(url);
            vm.StatusMessage = "Copied VRCDN URL to clipboard.";
        }
        else
        {
            vm.StatusMessage = "This photo hasn't been uploaded yet - no VRCDN URL to copy.";
        }
    }

    private void TagFaces_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not PhotoViewModel photo) return;
        if (DataContext is not MainViewModel vm) return;

        OpenTagFaces(vm, photo);
    }

    private void PhotoImage_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhotoViewModel photo)
        {
            photo.Selected = !photo.Selected;
        }
    }

    private void PhotoImage_MiddleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if ((sender as FrameworkElement)?.DataContext is not PhotoViewModel photo) return;
        if (DataContext is not MainViewModel vm) return;

        OpenTagFaces(vm, photo);
    }

    /// <summary>
    /// Tag Faces is now non-modal (Show, not ShowDialog - see DialogWindowBehavior) so the
    /// main window stays clickable while it's open. The face-count badge and player-filter
    /// refresh used to run synchronously right after ShowDialog() returned; now they have to
    /// wait for this specific window's Closed event instead.
    ///
    /// Deliberately no Owner: Win32 always keeps an owned window above its owner regardless of
    /// which one is actually focused, which fights directly against "click the main window to
    /// dismiss the dialog on top of it" (DialogWindowBehavior.CloseOnDeactivated) - clicking
    /// the main window would just get shoved behind the still-owned dialog instead of bringing
    /// it forward. Trade-off: this window no longer auto-closes if the main window closes
    /// first, and it gets its own independent taskbar/Alt+Tab entry.
    ///
    /// Singleton by design: since the main window is now clickable while Tag Faces is open (the
    /// whole point of the above), a second middle-click/context-menu action could otherwise
    /// spawn a second Tag Faces window - two windows racing over the same underlying face data
    /// isn't something the picker's local state (_pendingManualFaceId etc.) was ever designed
    /// to handle safely. If one's already open, this just brings it to the front instead of
    /// opening another (found via a real report of duplicate windows appearing).
    /// </summary>
    private void OpenTagFaces(MainViewModel vm, PhotoViewModel photo)
    {
        if (_openTagFacesWindow is not null)
        {
            _openTagFacesWindow.Activate();
            vm.StatusMessage = "A Tag Faces window is already open - close it before opening another.";
            return;
        }

        HidePreviewOverlay();
        var window = new Views.TagFacesWindow(vm.Faces, vm.Repo, vm.ProfileLookup, photo.Model);
        _openTagFacesWindow = window;
        window.Closed += (_, _) =>
        {
            _openTagFacesWindow = null;
            vm.ApplyFaceCounts();
            vm.RefreshPlayerFilterOptions();
            // The selected filter's DisplayText (e.g. a "(tagged)" suffix) can change as a
            // result of tagging - resync the box so it doesn't show stale text for the
            // still-active selection.
            SetPlayerFilterText(TextBoxTextFor(vm.SelectedPlayerFilter));
        };
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Player filter autocomplete - same search-as-you-type shape as Tag Faces' person picker
    /// (MainViewModel.SearchPlayerFilterOptions does the alias-aware fuzzy matching), but for a
    /// filter selection rather than a tag action: clicking a match commits it via
    /// SelectedPlayerFilter, and losing focus without picking anything reverts the box back to
    /// whatever filter is still actually active - a plain ComboBox with ~1800 players in
    /// alphabetical order was unusable for finding one specific person by name (found via a
    /// real report).
    /// </summary>
    private void PlayerFilterTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        PlayerFilterListBox.ItemsSource = vm.SearchPlayerFilterOptions(PlayerFilterTextBox.Text);
        PlayerFilterPopup.IsOpen = true;
        PlayerFilterTextBox.SelectAll();
    }

    /// <summary>"(all players)" is a real, clickable row in the dropdown (so typing "all"
    /// still finds it), but showing that literal text in the closed box read as if a player
    /// named "(all players)" were selected - blank (with the "All players" gray placeholder
    /// text behind it) reads as "no filter" the way an empty search box normally would.</summary>
    private static string TextBoxTextFor(MainViewModel.PlayerFilterOption option) =>
        option.VrcUserId is null && option.PersonId is null ? "" : option.DisplayText;

    private void SetPlayerFilterText(string text)
    {
        PlayerFilterTextBox.Text = text;
        PlayerFilterPlaceholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Deferred to the dispatcher's Background priority so a click that's selecting an item in
    /// PlayerFilterListBox - which also fires this LostFocus, since the popup is a separate
    /// hwnd - gets to run its own MouseUp handler first. By the time this runs,
    /// SelectedPlayerFilter (and this box's Text) already reflects any new choice, so
    /// reapplying it here is a harmless no-op in that case; it only actually changes anything
    /// when the user typed a search and then clicked away without picking a result, where it
    /// correctly reverts the stray typed text back to the filter that's still really active.
    /// </summary>
    private void PlayerFilterTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (DataContext is not MainViewModel vm) return;
            PlayerFilterPopup.IsOpen = false;
            SetPlayerFilterText(TextBoxTextFor(vm.SelectedPlayerFilter));
        });
    }

    private void PlayerFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        PlayerFilterPlaceholder.Visibility = PlayerFilterTextBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlayerFilterListBox.ItemsSource = vm.SearchPlayerFilterOptions(PlayerFilterTextBox.Text);
        PlayerFilterPopup.IsOpen = true;
    }

    private void PlayerFilterTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (DataContext is not MainViewModel vm) return;
        e.Handled = true;
        PlayerFilterPopup.IsOpen = false;
        SetPlayerFilterText(TextBoxTextFor(vm.SelectedPlayerFilter));
    }

    private void PlayerFilterListBox_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (PlayerFilterListBox.SelectedItem is not MainViewModel.PlayerFilterOption option) return;
        vm.SelectedPlayerFilter = option;
        SetPlayerFilterText(TextBoxTextFor(option));
        PlayerFilterPopup.IsOpen = false;
    }

    private void PhotoGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            // subtract scrollbar width so column count doesn't oscillate near the edge
            vm.GridWidth = e.NewSize.Width - 24;
        }
    }

    private void PhotoItem_MouseEnter(object sender, MouseEventArgs e) => ResetHoverTimer(sender as FrameworkElement);
    private void PhotoItem_MouseMove(object sender, MouseEventArgs e) => ResetHoverTimer(sender as FrameworkElement);

    private void PhotoItem_MouseLeave(object sender, MouseEventArgs e) => HidePreviewOverlay();

    /// <summary>Also called before opening any secondary window (About/Settings/Metadata/Tag
    /// Faces) - those windows now open positioned near the cursor (DialogWindowBehavior.
    /// OpenNearCursor), and popping up right on top of the hover preview looked cluttered.</summary>
    private void HidePreviewOverlay()
    {
        _hoverTimer.Stop();
        _hoverTarget = null;
        _currentPreviewPhoto = null;
        PreviewOverlay.Visibility = Visibility.Collapsed;
        // Clear any in-flight show animation so a rapid hover-away doesn't leave the overlay
        // stuck mid-grow the next time it appears - BeginAnimation(prop, null) reverts to the
        // element's plain (non-animated) property value.
        PreviewOverlay.BeginAnimation(UIElement.OpacityProperty, null);
        PreviewScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PreviewScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PreviewTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private static readonly IEasingFunction PreviewEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };
    private static readonly TimeSpan PreviewAnimDuration = TimeSpan.FromMilliseconds(180);

    /// <summary>Grows in from 85% scale + a slight upward slide instead of popping in at full
    /// size instantly - the instant version felt too sudden (found via direct feedback).</summary>
    private void AnimatePreviewOverlayIn()
    {
        PreviewOverlay.Visibility = Visibility.Visible;
        PreviewOverlay.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, PreviewAnimDuration) { EasingFunction = PreviewEase });
        PreviewScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.85, 1, PreviewAnimDuration) { EasingFunction = PreviewEase });
        PreviewScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.85, 1, PreviewAnimDuration) { EasingFunction = PreviewEase });
        PreviewTranslateTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(20, 0, PreviewAnimDuration) { EasingFunction = PreviewEase });
    }

    private void ResetHoverTimer(FrameworkElement? element)
    {
        if (element is null) return;
        _hoverTarget = element;
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void HoverTimer_Tick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        if (_hoverTarget?.DataContext is not PhotoViewModel photo) return;
        if (photo == _currentPreviewPhoto) return; // already showing this one - nothing changed

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 1000; // cap memory use - this is a preview, not for editing
            bmp.UriSource = new Uri(photo.Model.LocalPath);
            bmp.EndInit();
            bmp.Freeze();

            PreviewImage.Source = bmp;
            PreviewPlayers.Text = photo.PlayersTooltip;
            AnimatePreviewOverlayIn();
            _currentPreviewPhoto = photo;

            if (DataContext is MainViewModel vm && vm.AutoCopyUrlOnHover)
            {
                // Clipboard.SetText("") throws ArgumentException - empty string isn't a valid
                // clipboard text value, so a not-yet-uploaded photo needs Clear() instead.
                if (photo.RemoteUrl is string url) Clipboard.SetText(url);
                else Clipboard.Clear();
            }
        }
        catch
        {
            // full-res original may be missing/moved since scan, or the clipboard is
            // momentarily locked by another process - either way, just skip silently.
        }
    }
}

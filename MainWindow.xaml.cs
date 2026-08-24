using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VrcPhotoManager.Services;
using VrcPhotoManager.ViewModels;

namespace VrcPhotoManager;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _hoverTimer;
    private FrameworkElement? _hoverTarget;
    private Views.TagFacesWindow? _openTagFacesWindow;
    private Views.FilterWindow? _openFilterWindow;

    /// <summary>The photo the preview overlay is currently showing, if any - guards
    /// HoverTimer_Tick against redoing the grow-in animation from scratch if its timer somehow
    /// ticks again for a photo it's already showing (found via a real report of the animation
    /// restarting mid-hover, back when MouseMove also restarted the timer - see
    /// ResetHoverTimer's doc comment for why only MouseEnter does now).</summary>
    private PhotoViewModel? _currentPreviewPhoto;

    /// <summary>Tracks rapid-succession plain-scroll wheel notches for HandleRowScroll's
    /// acceleration - see its doc comment.</summary>
    private DateTime _lastRowScrollTime;
    private int _lastRowScrollDelta;
    private int _rowScrollStreak;

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainViewModel();
        DataContext = viewModel;
        RestoreWindowBounds(viewModel);

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.25) };
        _hoverTimer.Tick += HoverTimer_Tick;

        viewModel.ToastRequested += ShowToast;
        // See MainViewModel.RowsRebuilt's doc comment - container recycling can silently
        // reassign a stale _hoverTarget to the wrong photo after a rebuild (e.g. right after an
        // upload) without ever firing MouseLeave on it first.
        viewModel.RowsRebuilt += (_, _) => HidePreviewOverlay();
        // The very next StatusMessage change after this subscribes is guaranteed to be
        // InitializeAsync's "N photos loaded." (MainViewModel's constructor already set
        // "Loading..." and returned before this line runs, so that first assignment doesn't
        // count) - a reliable one-shot signal that the initial RebuildRows has synchronously
        // finished and Rows actually has content to scroll into, without depending on Rows'
        // own CollectionChanged noise (RebuildRows Adds one row at a time, so the first Add
        // alone doesn't mean the full set is there yet).
        viewModel.PropertyChanged += RestoreScrollPositionOnce;
        Closing += (_, _) =>
        {
            SaveSessionState(viewModel);
            viewModel.RequestShutdown();
        };
        PreviewMouseWheel += MainWindow_PreviewMouseWheel;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    /// <summary>Applied before the window is shown - MainWindow.xaml's Height="900" Width="1400"
    /// are just the first-ever-launch defaults, left untouched when no prior session exists.</summary>
    private void RestoreWindowBounds(MainViewModel vm)
    {
        double savedWidth = vm.Repo.GetDoubleSetting(SettingsKeys.WindowWidth, 0);
        double savedHeight = vm.Repo.GetDoubleSetting(SettingsKeys.WindowHeight, 0);
        if (savedWidth > 0 && savedHeight > 0)
        {
            Width = savedWidth;
            Height = savedHeight;
        }
        if (vm.Repo.GetBoolSetting(SettingsKeys.WindowMaximized))
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveSessionState(MainViewModel vm)
    {
        bool maximized = WindowState == WindowState.Maximized;
        vm.Repo.SetBoolSetting(SettingsKeys.WindowMaximized, maximized);
        // RestoreBounds gives the pre-maximize size even while currently maximized - saving
        // ActualWidth/Height instead would persist the maximized (near-screen-size) dimensions,
        // which would then get treated as the "normal" size the next time the window
        // un-maximizes.
        Rect bounds = maximized ? RestoreBounds : new Rect(Left, Top, ActualWidth, ActualHeight);
        vm.Repo.SetDoubleSetting(SettingsKeys.WindowWidth, bounds.Width);
        vm.Repo.SetDoubleSetting(SettingsKeys.WindowHeight, bounds.Height);

        vm.Repo.SetDoubleSetting(SettingsKeys.LastThumbnailSize, vm.ThumbnailSize);
        vm.SaveFilterState();

        if (FindVisualChild<ScrollViewer>(PhotoGrid) is ScrollViewer scrollViewer)
        {
            vm.Repo.SetDoubleSetting(SettingsKeys.LastScrollOffset, scrollViewer.VerticalOffset);
        }
    }

    private void RestoreScrollPositionOnce(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.StatusMessage)) return;
        if (DataContext is not MainViewModel vm) return;
        vm.PropertyChanged -= RestoreScrollPositionOnce;

        double savedOffset = vm.Repo.GetDoubleSetting(SettingsKeys.LastScrollOffset, 0);
        if (savedOffset <= 0) return;

        // Deferred to Loaded priority - Rows is already fully populated by this point, but the
        // virtualizing panel still needs an actual layout pass before ScrollableHeight reflects
        // it (same reasoning as the Alt+resize handler's own Loaded-priority defer).
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            PhotoGrid.UpdateLayout();
            if (FindVisualChild<ScrollViewer>(PhotoGrid) is ScrollViewer scrollViewer)
            {
                scrollViewer.ScrollToVerticalOffset(savedOffset);
            }
        });
    }

    /// <summary>Pressing Alt alone starts WPF's internal menu-access-key gesture (it's how
    /// Alt+letter mnemonics work), which captures/swallows subsequent input - including mouse
    /// wheel - until something (typically a click) cancels it. That's a much better fit for the
    /// "scrolling needs a click after Alt+wheel-resize" symptom than stale hit-testing was: the
    /// Mouse.Synchronize() deferral in MainWindow_PreviewMouseWheel targeted hit-testing and
    /// never actually fixed it. Marking the Alt key event handled here stops that gesture from
    /// starting in the first place, for Alt+wheel or a bare Alt tap.</summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.System && (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (DataContext is MainViewModel vm) OpenFilterWindow(vm);
        }

        // Nudges the upload crop position on whichever photo is currently hovered (see
        // PhotoViewModel.NudgeCropOffset, which reverts an already-Uploaded photo back to
        // NotUploaded first rather than no-opping) - _hoverTarget is the same FrameworkElement
        // ResetHoverTimer/HidePreviewOverlay already track for the hover-preview popup, so
        // there's no separate "which photo is under the cursor" bookkeeping to add. Skipped
        // while typing in a text box (e.g. the custom crop-ratio field) so arrow keys still
        // move the text cursor there instead of being hijacked just because a photo happens
        // to be hovered at the same time. Also dismisses the hover-preview popup (without
        // clearing _hoverTarget - see HidePreviewOverlay's doc comment) since it covers the
        // thumbnail grid, right where you'd want to see the crop lines actually move.
        if ((e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            && Keyboard.FocusedElement is not TextBox
            && _hoverTarget?.DataContext is PhotoViewModel hoveredPhoto)
        {
            HidePreviewOverlay(clearHoverTarget: false);
            int dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
            int dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
            hoveredPhoto.NudgeCropOffset(dx, dy);
            e.Handled = true;
        }

        // [ / ] cycle the hovered photo's own crop-ratio preset instead of the position within
        // it - see PhotoViewModel.CycleCropRatioOverride. Same hover-target/text-box/popup
        // handling as the arrow-key nudge above.
        if ((e.Key is Key.OemOpenBrackets or Key.OemCloseBrackets)
            && Keyboard.FocusedElement is not TextBox
            && _hoverTarget?.DataContext is PhotoViewModel hoveredForRatio)
        {
            HidePreviewOverlay(clearHoverTarget: false);
            int direction = e.Key == Key.OemCloseBrackets ? 1 : -1;
            hoveredForRatio.CycleCropRatioOverride(direction, MainViewModel.UploadCropPresets);
            e.Handled = true;
        }
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) OpenFilterWindow(vm);
    }

    /// <summary>Singleton by the same reasoning as OpenTagFaces - not for data-safety here (the
    /// filter window has no local state of its own, everything's a live binding to vm), just to
    /// avoid window clutter from Ctrl+F/the button spawning a new one every time. No Owner, same
    /// reasoning as OpenTagFaces too.</summary>
    private void OpenFilterWindow(MainViewModel vm)
    {
        if (_openFilterWindow is not null)
        {
            _openFilterWindow.Activate();
            return;
        }

        var window = new Views.FilterWindow(vm);
        _openFilterWindow = window;
        window.Closed += (_, _) => _openFilterWindow = null;
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Alt+scroll resizes thumbnails instead of scrolling the grid - PreviewMouseWheel
    /// (tunneling) so this fires before PhotoGrid's own wheel-scroll handling, and only marks
    /// the event Handled when Alt is actually held, so a plain scroll still scrolls normally.
    /// Matches the Slider's own Minimum="80"/Maximum="400" range (see MainWindow.xaml) - a
    /// direct property set isn't clamped by the Slider the way a drag would be.
    ///
    /// Resizing changes both the row height AND the column count (items per row shrinks as
    /// thumbnails grow), so both the row AND the column a given photo ends up in shift - without
    /// correction, the grid visibly jumps in both directions and whatever was under the cursor
    /// is gone. Hit-test which photo is under the cursor *before* resizing and remember how far
    /// down it (0-1 fraction) the cursor was, plus which column the cursor is over.
    ///
    /// Vertical correction: every row is exactly ThumbnailSize+RowMargin tall (every cell -
    /// including BlankCellTemplate - is sized to that, see MainWindow.xaml), so the anchor row's
    /// absolute scroll offset is just newRowIndex * newCellSize - no need to realize the row's
    /// container and read back its real position. An earlier version called ScrollIntoView
    /// first to get a real measured position via TranslatePoint - that worked, but ScrollIntoView
    /// itself scrolls instantly, so by the time the animation ran there was nothing perceptible
    /// left for it to smooth (confirmed via direct feedback: "doesn't seem smooth yet"). The
    /// analytic offset needs no realized container, so nothing has to jump ahead of the
    /// animation.
    ///
    /// Horizontal correction: there's no horizontal ScrollViewer to lean on (the grid always
    /// fits width and wraps), so leading blank placeholder cells (null entries in row 0's
    /// Items - see PhotoRow's doc comment and MainViewModel.RebuildRowsWithLeadingPadding) are
    /// used instead - just enough of them that the anchor photo's flat index falls into the
    /// desired column once re-chunked. A per-row invisible spacer was tried first instead of
    /// placeholder cells - it never broke anything, but looked visually off (direct feedback),
    /// so this reverts to the originally-requested placeholder-cell approach.
    /// </summary>
    private void MainWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // This is a Window-level handler (tunnels down from the window before the actual
        // control under the cursor sees the event), so without this check it steals wheel
        // input meant for anything else - ComboBox dropdowns, the player-filter popup - and
        // resize/scrolls the main grid instead (found via direct report: "search comboboxes
        // aren't scrollable anymore"). Only handle the wheel here when the cursor is actually
        // over the photo grid itself.
        if (e.OriginalSource is not DependencyObject source || !IsDescendantOf(source, PhotoGrid)) return;

        if (Keyboard.Modifiers != ModifierKeys.Alt)
        {
            HandleRowScroll(vm, e);
            return;
        }

        e.Handled = true;

        // Hides the preview overlay for the duration of active resizing rather than letting it
        // pop up mid-resize - each Alt+scroll notch re-hides it before the hover timer's 0.25s
        // delay can complete, so it only reappears once you've actually stopped scrolling and
        // settled on a photo. Also sidesteps _hoverTarget going stale when the row it points at
        // gets rebuilt (RebuildRowsWithLeadingPadding below), same as it always would for any
        // Rows change.
        HidePreviewOverlay();

        Point cursor = e.GetPosition(PhotoGrid);
        var anchor = FindAnchor(vm, cursor);

        double step = e.Delta > 0 ? 20 : -20;
        vm.ThumbnailSize = Math.Clamp(vm.ThumbnailSize + step, 80, 400);

        if (anchor is null) return;
        int flatIndex = anchor.Value;

        int newColumns = Math.Max(1, (int)(vm.GridWidth / (vm.ThumbnailSize + MainViewModel.RowMargin)));
        double newCellSize = vm.ThumbnailSize + MainViewModel.RowMargin;

        int desiredCol = Math.Clamp((int)(cursor.X / newCellSize), 0, newColumns - 1);
        int leadingBlanks = ((desiredCol - flatIndex) % newColumns + newColumns) % newColumns;
        // ThumbnailSize's setter above already triggered one (unpadded) RebuildRows - this
        // replaces it with the padded version. The extra rebuild is a small, one-off cost per
        // scroll notch, not worth restructuring ThumbnailSize's setter contract over (the
        // Slider uses the same setter and has no need for padding logic at all).
        vm.RebuildRowsWithLeadingPadding(leadingBlanks);

        int newRowIndex = (leadingBlanks + flatIndex) / newColumns;

        // Deferred to Loaded priority - the Rows collection just changed synchronously above,
        // but the virtualizing panel needs an actual layout pass (container generation for the
        // new column count/item size) before ScrollIntoView has anything real to scroll to.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (newRowIndex < 0 || newRowIndex >= vm.Rows.Count) return;
            if (FindVisualChild<ScrollViewer>(PhotoGrid) is not ScrollViewer scrollViewer) return;

            PhotoGrid.UpdateLayout();

            // Snaps the anchor row to the top of the viewport rather than trying to keep the
            // cursor at the same fractional pixel position within it - simpler, and reads
            // better per direct feedback than the fractional version did. Clamped to
            // ScrollableHeight so a resize near the bottom of the list can't request an offset
            // past what's actually scrollable.
            double targetOffset = Math.Clamp(newRowIndex * newCellSize, 0, scrollViewer.ScrollableHeight);

            // Programmatic scrolling moves content out from under a stationary cursor without
            // WPF's internal "what's under the mouse" tracking noticing on its own - it stays
            // stale until some other input (a click, a hover onto a different element) forces a
            // recompute, which is why plain scrolling needed a click first afterward (confirmed
            // via direct feedback). Mouse.Synchronize() is the standard WPF workaround, but it
            // has to run once the scroll has actually landed at its final position - with the
            // animated scroll below, that's the animation's Completed event, not a fixed defer.
            ScrollAnimation.AnimateTo(scrollViewer, targetOffset, PreviewAnimDuration, PreviewEase,
                onCompleted: Mouse.Synchronize);
        });
    }

    /// <summary>How many consecutive notches (within RowScrollStreakWindow, same direction) it
    /// takes to reach the maximum per-notch row multiplier.</summary>
    private const int RowScrollMaxStreak = 8;
    private const double RowScrollMaxMultiplier = 7.0;
    private static readonly TimeSpan RowScrollStreakWindow = TimeSpan.FromMilliseconds(350);

    /// <summary>Plain (non-Alt) wheel scrolling snaps to whole thumbnail rows instead of WPF's
    /// default fixed-line scroll amount, and animates the move the same way the Alt+resize
    /// correction does (same helper, same easing) - a run of wheel notches re-seeds each new
    /// animation from the ScrollViewer's real (already-interpolating) offset, so it reads as one
    /// continuous smooth scroll rather than a series of discrete jumps.
    ///
    /// Accelerates on rapid successive notches in the same direction (a real flick of the wheel
    /// rather than one deliberate click), scaling the row multiplier up to RowScrollMaxMultiplier
    /// over RowScrollMaxStreak notches so a fast scroll covers ground quickly instead of crawling
    /// one row at a time. Any pause longer than RowScrollStreakWindow, or a direction reversal,
    /// resets the streak back to a single row - deliberate single clicks stay precise.</summary>
    private void HandleRowScroll(MainViewModel vm, MouseWheelEventArgs e)
    {
        if (FindVisualChild<ScrollViewer>(PhotoGrid) is not ScrollViewer scrollViewer) return;
        e.Handled = true;

        int delta = Math.Sign(e.Delta);
        DateTime now = DateTime.UtcNow;
        bool continuesStreak = delta == _lastRowScrollDelta && now - _lastRowScrollTime < RowScrollStreakWindow;
        _rowScrollStreak = continuesStreak ? Math.Min(_rowScrollStreak + 1, RowScrollMaxStreak) : 0;
        _lastRowScrollDelta = delta;
        _lastRowScrollTime = now;

        double multiplier = 1 + (RowScrollMaxMultiplier - 1) * _rowScrollStreak / RowScrollMaxStreak;
        double rowHeight = vm.ThumbnailSize + MainViewModel.RowMargin;
        double targetOffset = Math.Clamp(
            scrollViewer.VerticalOffset - delta * rowHeight * multiplier,
            0, scrollViewer.ScrollableHeight);

        ScrollAnimation.AnimateTo(scrollViewer, targetOffset, PreviewAnimDuration, PreviewEase);
    }

    /// <summary>Hit-tests PhotoGrid at the given point (walking up from whatever the point-test
    /// actually hit - an Image, a Border, etc. - to the enclosing element carrying the
    /// PhotoViewModel DataContext) to find which photo the cursor is over. Null if the cursor
    /// isn't over any realized photo item (e.g. over empty space past the last row).</summary>
    private FrameworkElement? FindAnchorElement(Point cursorInGrid, out PhotoViewModel? target)
    {
        target = null;
        var element = PhotoGrid.InputHitTest(cursorInGrid) as DependencyObject;
        while (element is not null && (element as FrameworkElement)?.DataContext is not PhotoViewModel)
        {
            element = VisualTreeHelper.GetParent(element);
        }
        if (element is not FrameworkElement found || found.DataContext is not PhotoViewModel photo) return null;
        target = photo;
        return found;
    }

    /// <summary>Finds which photo is under cursorInGrid (already in PhotoGrid's own coordinate
    /// space, e.g. from e.GetPosition(PhotoGrid)) and that photo's flat index in the current
    /// Rows (summed across preceding rows rather than assumed from a fixed column count, since
    /// the last row can be short). Null if FindAnchorElement found nothing under the cursor.
    ///
    /// Only the flat index is needed - the resize correction snaps the anchor's row to the top
    /// of the viewport rather than trying to keep the cursor at the same fractional pixel
    /// position within it (an earlier version returned FractionY/FractionX for that finer
    /// correction; per direct feedback, per-row snapping reads better than the fractional
    /// version did).</summary>
    private int? FindAnchor(MainViewModel vm, Point cursorInGrid)
    {
        var element = FindAnchorElement(cursorInGrid, out PhotoViewModel? target);
        if (element is null || target is null) return null;

        int flatIndex = 0;
        foreach (var row in vm.Rows)
        {
            int indexInRow = row.Items.ToList().IndexOf(target);
            if (indexInRow >= 0) return flatIndex + indexInRow;
            flatIndex += row.Items.Count;
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            if (FindVisualChild<T>(child) is T found) return found;
        }
        return null;
    }

    /// <summary>Walks up the visual tree from element looking for ancestor. Popup content (a
    /// ComboBox dropdown, PlayerFilterPopup's list) has no visual-tree path back to the main
    /// window's content - VisualTreeHelper.GetParent hits null at the popup boundary - so this
    /// correctly returns false for anything rendered inside one.</summary>
    private static bool IsDescendantOf(DependencyObject? element, DependencyObject ancestor)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, ancestor)) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
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
        new Views.SettingsWindow(vm.Repo, vm.AvatarCatalog).Show();
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
        CopyVrcdnUrlToClipboard(vm, photo);
    }

    /// <summary>Also the cloud-icon badge's click handler (CloudIcon_MouseLeftButtonUp) - that
    /// badge only ever shows when RemoteStatus is Uploaded (see MainWindow.xaml's
    /// RemoteStatusToVisibilityConverter usage), so its RemoteUrl is always non-null in
    /// practice, but the null-check/status-message fallback is kept shared here anyway rather
    /// than duplicated, in case that ever changes.</summary>
    private void CopyVrcdnUrlToClipboard(MainViewModel vm, PhotoViewModel photo)
    {
        if (photo.RemoteUrl is string url)
        {
            Clipboard.SetText(url);
            vm.StatusMessage = "Copied VRCDN URL to clipboard.";
            vm.ShowToast("Copied VRCDN URL to clipboard.");
        }
        else
        {
            vm.StatusMessage = "This photo hasn't been uploaded yet - no VRCDN URL to copy.";
        }
    }

    private void CloudIcon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PhotoViewModel photo) return;
        if (DataContext is not MainViewModel vm) return;
        e.Handled = true;
        CopyVrcdnUrlToClipboard(vm, photo);
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
        var (pathByPhotoId, avatarTypeByPhotoId) = vm.GetPhotoPathsAndAvatarTypes();
        var window = new Views.TagFacesWindow(vm.Faces, vm.Repo, vm.AvatarRegions, vm.AvatarCatalog, vm.AvatarClassifier, vm.ProfileLookup, photo.Model,
            vm.CcipEmbedder, vm.GetVisiblePhotoIds(), pathByPhotoId, avatarTypeByPhotoId,
            vm.SuggestionsMayBeStale, stale => vm.SuggestionsMayBeStale = stale);
        _openTagFacesWindow = window;
        window.Closed += (_, _) =>
        {
            _openTagFacesWindow = null;
            vm.ApplyFaceCounts();
            vm.RefreshPlayerFilterOptions();
            // The selected filter's DisplayText (e.g. a "(tagged)" suffix) can change as a
            // result of tagging - resync the box so it doesn't show stale text for the
            // still-active selection.
            PlayerFilterPicker.SyncDisplayText();
        };
        window.Show();
        window.Activate();
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

    private void PhotoItem_MouseLeave(object sender, MouseEventArgs e) => HidePreviewOverlay();

    /// <summary>Also called before opening any secondary window (About/Settings/Metadata/Tag
    /// Faces) - those windows now open positioned near the cursor (DialogWindowBehavior.
    /// OpenNearCursor), and popping up right on top of the hover preview looked cluttered.
    /// clearHoverTarget=false is for the crop-nudge keyboard handler: the big preview popup
    /// obscures the thumbnail grid, so a crop nudge dismisses it too, but MUST keep _hoverTarget
    /// intact - clearing it would make the very next arrow-key press find no hovered photo at
    /// all, breaking repeated nudges (no MouseEnter fires again just from holding still and
    /// pressing keys).</summary>
    private void HidePreviewOverlay(bool clearHoverTarget = true)
    {
        _hoverTimer.Stop();
        if (clearHoverTarget) _hoverTarget = null;
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

    /// <summary>MainViewModel.ToastRequested handler - fades ToastOverlay in, holds it, then
    /// fades it out, all as one KeyFrame animation so a second toast arriving mid-animation
    /// (BeginAnimation replaces the running one outright) just restarts the same cycle with the
    /// new text instead of needing separate timer bookkeeping to cancel/reschedule anything.</summary>
    private void ShowToast(string message)
    {
        ToastText.Text = message;
        var opacity = new DoubleAnimationUsingKeyFrames();
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2200))));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600))));
        ToastOverlay.BeginAnimation(UIElement.OpacityProperty, opacity);
    }

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

    /// <summary>Only called from MouseEnter now, deliberately not MouseMove too - it used to
    /// also restart on every MouseMove within the same photo, which meant ordinary cursor
    /// jitter while waiting for the preview to appear kept resetting the countdown before it
    /// ever completed a full uninterrupted 0.25s (found via direct feedback: the preview
    /// sometimes never popped up at all). Once it HAS shown, further movement within the same
    /// photo doesn't need any reset either - HoverTimer_Tick's _currentPreviewPhoto check
    /// already makes a stray tick for the same photo a no-op, and nothing but MouseLeave hides
    /// it, so it just stays shown regardless of small in-item movement.</summary>
    private void ResetHoverTimer(FrameworkElement? element)
    {
        if (element is null) return;
        _hoverTarget = element;
        _hoverTimer.Stop();
        // Read live (not cached at construction) so a change in Settings' preview-delay slider
        // takes effect on the very next hover, no restart needed - matches AutoCopyUrlOnHover's
        // "read fresh every time" convention.
        if (DataContext is MainViewModel vm) _hoverTimer.Interval = TimeSpan.FromSeconds(vm.HoverPreviewDelaySeconds);
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

/// <summary>ScrollViewer.VerticalOffset is a plain CLR property, not a DependencyProperty, so it
/// can't be targeted by a WPF DoubleAnimation directly - this attached property exists purely as
/// an animatable proxy: animating it drives ScrollToVerticalOffset on every frame via
/// OnAnimatedOffsetChanged.</summary>
internal static class ScrollAnimation
{
    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.RegisterAttached(
        "AnimatedOffset", typeof(double), typeof(ScrollAnimation),
        new PropertyMetadata(0.0, OnAnimatedOffsetChanged));

    private static void OnAnimatedOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer scrollViewer) scrollViewer.ScrollToVerticalOffset((double)e.NewValue);
    }

    public static void AnimateTo(ScrollViewer scrollViewer, double targetOffset, TimeSpan duration,
        IEasingFunction easing, Action? onCompleted = null)
    {
        // Seeded from the ScrollViewer's real current offset (not the attached property's own
        // stale value) since nothing else drives AnimatedOffsetProperty - without this, a
        // second resize mid-animation would start the new animation from wherever the last one
        // left the proxy property, not from where the ScrollViewer actually is. Same reasoning
        // makes back-to-back plain-scroll notches (HandleRowScroll) chain smoothly instead of
        // each one restarting from a stale position.
        var animation = new DoubleAnimation(scrollViewer.VerticalOffset, targetOffset, duration)
        {
            EasingFunction = easing
        };
        if (onCompleted is not null) animation.Completed += (_, _) => onCompleted();
        scrollViewer.BeginAnimation(AnimatedOffsetProperty, animation);
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VrcPhotoManager.ViewModels;

namespace VrcPhotoManager;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _hoverTimer;
    private FrameworkElement? _hoverTarget;

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainViewModel();
        DataContext = viewModel;

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.25) };
        _hoverTimer.Tick += HoverTimer_Tick;

        Closing += (_, _) => viewModel.RequestShutdown();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        new Views.AboutWindow().Show();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        new Views.SettingsWindow(vm.Repo).Show();
    }

    private void ViewMetadata_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not PhotoViewModel photo) return;
        new Views.MetadataWindow(photo).Show();
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
    /// </summary>
    private void OpenTagFaces(MainViewModel vm, PhotoViewModel photo)
    {
        var window = new Views.TagFacesWindow(vm.Faces, vm.Repo, vm.ProfileLookup, photo.Model);
        window.Closed += (_, _) =>
        {
            vm.ApplyFaceCounts();
            vm.RefreshPlayerFilterOptions();
        };
        window.Show();
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

    private void PhotoItem_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer.Stop();
        _hoverTarget = null;
        PreviewOverlay.Visibility = Visibility.Collapsed;
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
            PreviewOverlay.Visibility = Visibility.Visible;

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

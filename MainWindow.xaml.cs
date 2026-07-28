using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VrcdnManager.ViewModels;

namespace VrcdnManager;

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
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        new Views.AboutWindow { Owner = this }.ShowDialog();
    }

    private void ViewMetadata_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not PhotoViewModel photo) return;
        new Views.MetadataWindow(photo) { Owner = this }.ShowDialog();
    }

    private void PhotoImage_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhotoViewModel photo)
        {
            photo.Selected = !photo.Selected;
        }
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
        PreviewPopup.IsOpen = false;
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
            PreviewPopup.PlacementTarget = _hoverTarget;
            PreviewPopup.IsOpen = true;
        }
        catch
        {
            // full-res original may be missing/moved since scan - just skip the preview
        }
    }
}

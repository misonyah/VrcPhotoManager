using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VrcPhotoManager.ViewModels;

/// <summary>
/// A row of photos for the grid. Grouping the flat photo list into fixed-size rows lets the
/// gallery reuse WPF's native VirtualizingStackPanel (vertical) instead of needing a custom
/// virtualizing wrap panel - each row is one virtualized list item, so off-screen rows never
/// realize their child Image controls or decode their thumbnails.
/// </summary>
public class PhotoRow : INotifyPropertyChanged
{
    public IReadOnlyList<PhotoViewModel> Items { get; }

    /// <summary>Width of an invisible leading spacer rendered before this row's items (see
    /// MainWindow.xaml's PhotoRow DataTemplate) - always 0 for a normally-built row. Only
    /// MainWindow's Alt+scroll resize handler ever sets this, as a one-off pixel-precise nudge
    /// so the specific row containing the photo under the cursor lines that photo up with the
    /// cursor's X position after a resize changes the column count (there's no horizontal
    /// ScrollViewer to use for this the way vertical position uses one).
    ///
    /// Deliberately a plain Border spacer OUTSIDE the row's data-bound Items, not a null
    /// placeholder mixed into Items itself - an earlier version tried the latter (leading blank
    /// placeholder cells inside the virtualized ItemsSource, via a DataTemplateSelector) and it
    /// corrupted WPF's virtualization badly enough to break plain mouse-wheel scrolling entirely
    /// (confirmed via live bisection: removing every OTHER change and keeping only that padding
    /// still broke input). This spacer approach never showed that failure mode.</summary>
    private double _leadingOffset;
    public double LeadingOffset
    {
        get => _leadingOffset;
        set
        {
            if (_leadingOffset == value) return;
            _leadingOffset = value;
            OnPropertyChanged();
        }
    }

    public PhotoRow(IReadOnlyList<PhotoViewModel> items)
    {
        Items = items;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

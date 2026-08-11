namespace VrcPhotoManager.ViewModels;

/// <summary>
/// A row of photos for the grid. Grouping the flat photo list into fixed-size rows lets the
/// gallery reuse WPF's native VirtualizingStackPanel (vertical) instead of needing a custom
/// virtualizing wrap panel - each row is one virtualized list item, so off-screen rows never
/// realize their child Image controls or decode their thumbnails.
///
/// Items is object? rather than PhotoViewModel because MainWindow's Alt+scroll resize handler
/// can insert leading blank placeholder cells (null entries) into row 0 - see
/// MainViewModel.RebuildRowsWithLeadingPadding - to keep the row a given photo lands in after a
/// resize as close as possible to where it was before, without disturbing any row's real
/// membership (every real photo stays grouped with the same neighbors; only the very first row
/// absorbs the padding needed to shift everything after it into alignment). The XAML
/// PhotoOrBlankTemplateSelector renders a null entry as an appropriately-sized empty cell.
/// </summary>
public class PhotoRow
{
    public IReadOnlyList<object?> Items { get; }

    public PhotoRow(IReadOnlyList<object?> items)
    {
        Items = items;
    }
}

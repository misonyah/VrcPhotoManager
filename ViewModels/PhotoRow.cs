namespace VrcdnManager.ViewModels;

/// <summary>
/// A row of photos for the grid. Grouping the flat photo list into fixed-size rows lets the
/// gallery reuse WPF's native VirtualizingStackPanel (vertical) instead of needing a custom
/// virtualizing wrap panel - each row is one virtualized list item, so off-screen rows never
/// realize their child Image controls or decode their thumbnails.
/// </summary>
public class PhotoRow
{
    public IReadOnlyList<PhotoViewModel> Items { get; }

    public PhotoRow(IReadOnlyList<PhotoViewModel> items)
    {
        Items = items;
    }
}

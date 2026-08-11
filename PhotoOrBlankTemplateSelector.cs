using System.Windows;
using System.Windows.Controls;
using VrcPhotoManager.ViewModels;

namespace VrcPhotoManager;

/// <summary>Picks PhotoTemplate for a real PhotoViewModel row item, BlankTemplate for a null
/// one (a leading placeholder cell - see PhotoRow's doc comment and
/// MainViewModel.RebuildRowsWithLeadingPadding).</summary>
public class PhotoOrBlankTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PhotoTemplate { get; set; }
    public DataTemplate? BlankTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
        item is PhotoViewModel ? PhotoTemplate : BlankTemplate;
}

using System.Windows;
using System.Windows.Input;
using VrcPhotoManager.ViewModels;

namespace VrcPhotoManager.Views;

/// <summary>Standalone panel duplicating MainWindow's filter bar controls, all bound directly
/// to the same MainViewModel instance - editing a filter here changes the exact same properties
/// the main filter bar reads, so the two stay in sync live with no extra plumbing. Opened via
/// Ctrl+F or the "Filter" button (see MainWindow.OpenFilterWindow).</summary>
public partial class FilterWindow : Window
{
    public FilterWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        PreviewKeyDown += FilterWindow_PreviewKeyDown;
    }

    /// <summary>Escape closes the window outright - same precedent as MetadataWindow/
    /// TagFacesWindow.</summary>
    private void FilterWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ClearFilters();
    }
}

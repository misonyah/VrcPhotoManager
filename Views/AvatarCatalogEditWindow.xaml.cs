using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VrcPhotoManager.Data;
using VrcPhotoManager.Models;

namespace VrcPhotoManager.Views;

/// <summary>Edits one AvatarCatalog row's store links (Booth/Gumroad/Jinxxy) and parent-avatar
/// lineage - see docs/superpowers/VrcPhotoManager/specs/2026-08-23-avatar-catalog-design.md (PC
/// umbrella repo) for the full design. Opened from TagFacesWindow's avatar picker via a new
/// "Edit catalog info" affordance, scoped to whichever entry is currently selected/tagged.</summary>
public partial class AvatarCatalogEditWindow : Window
{
    private readonly AvatarCatalogRepository _repo;
    private readonly long _catalogId;
    private long? _parentItemId;

    public AvatarCatalogEditWindow(AvatarCatalogRepository repo, long catalogId)
    {
        InitializeComponent();
        DialogWindowBehavior.HideMinimizeAndMaximizeButtons(this);
        DialogWindowBehavior.OpenNearCursor(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };

        _repo = repo;
        _catalogId = catalogId;

        var entry = repo.GetById(catalogId) ?? throw new InvalidOperationException($"AvatarCatalog row {catalogId} not found.");
        AvatarNameText.Text = entry.DisplayName ?? "(unnamed)";
        BoothProductBox.Text = entry.BoothProduct ?? "";
        GumroadUserBox.Text = entry.GumroadUser ?? "";
        GumroadProductBox.Text = entry.GumroadProduct ?? "";
        JinxxyUserBox.Text = entry.JinxxyUser ?? "";
        JinxxyProductBox.Text = entry.JinxxyProduct ?? "";

        _parentItemId = entry.ParentItemId;
        if (_parentItemId is long parentId)
        {
            ParentSearchTextBox.Text = repo.GetById(parentId)?.DisplayName ?? "(unknown)";
        }
    }

    // Sentinel id for the synthetic "Create new: '...'" row appended to search results - no
    // real AvatarCatalog.Id is ever <= 0 (SQLite autoincrement starts at 1), so this can never
    // collide with an actual row.
    private const long CreateNewSentinelId = -1;

    private void ParentSearchTextBox_GotFocus(object sender, RoutedEventArgs e) => RefreshParentSearch();

    private void ParentSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Typing invalidates whatever parent was previously selected - Save only commits a
        // parent link the user re-confirms by picking it (or creating a new entry) from the
        // list below, same "typing means not-yet-resolved" convention as PersonPickerPopup.
        _parentItemId = null;
        RefreshParentSearch();
    }

    private void RefreshParentSearch()
    {
        string query = ParentSearchTextBox.Text;
        var matches = _repo.Search(query).Where(c => c.Id != _catalogId).ToList();
        bool hasExactMatch = matches.Any(c =>
            string.Equals(c.DisplayName, query, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query) && !hasExactMatch)
        {
            matches.Add(new AvatarCatalog { Id = CreateNewSentinelId, DisplayName = $"Create new: '{query.Trim()}'" });
        }
        ParentSearchListBox.ItemsSource = matches;
        ParentSearchPopup.IsOpen = true;
    }

    private void ParentSearchListBox_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (ParentSearchListBox.SelectedItem is not AvatarCatalog selected) return;

        if (selected.Id == CreateNewSentinelId)
        {
            var created = _repo.CreateManualEntry(ParentSearchTextBox.Text.Trim());
            _parentItemId = created.Id;
            ParentSearchTextBox.Text = created.DisplayName ?? "";
        }
        else
        {
            _parentItemId = selected.Id;
            ParentSearchTextBox.Text = selected.DisplayName ?? "(unnamed)";
        }
        ParentSearchPopup.IsOpen = false;
    }

    private void ClearParentButton_Click(object sender, RoutedEventArgs e)
    {
        _parentItemId = null;
        ParentSearchTextBox.Text = "";
        ParentSearchPopup.IsOpen = false;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _repo.Update(_catalogId,
            boothProduct: NullIfBlank(BoothProductBox.Text),
            gumroadUser: NullIfBlank(GumroadUserBox.Text),
            gumroadProduct: NullIfBlank(GumroadProductBox.Text),
            jinxxyUser: NullIfBlank(JinxxyUserBox.Text),
            jinxxyProduct: NullIfBlank(JinxxyProductBox.Text),
            parentItemId: _parentItemId);
        DialogResult = true;
        Close();
    }

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}

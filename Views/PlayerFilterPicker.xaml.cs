using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VrcPhotoManager.ViewModels;

namespace VrcPhotoManager.Views;

/// <summary>
/// Player filter autocomplete - search-as-you-type shape (MainViewModel.SearchPlayerFilterOptions
/// does the alias-aware fuzzy matching): clicking (or Tab/Enter-ing) a match commits it via
/// SelectedPlayerFilter, and losing focus without picking anything reverts the box back to
/// whatever filter is still actually active - a plain ComboBox with ~1800 players in
/// alphabetical order was unusable for finding one specific person by name (found via a real
/// report). Extracted into a UserControl so both MainWindow's filter bar and FilterWindow's
/// standalone panel share one implementation instead of two copies of this code-behind - both
/// just need DataContext set to the same MainViewModel; nothing else to wire up.
/// </summary>
public partial class PlayerFilterPicker : UserControl
{
    public PlayerFilterPicker()
    {
        InitializeComponent();

        // The displayed text is set imperatively (SetPlayerFilterText), not a live binding - so
        // without this subscription, committing a player filter in one instance (e.g.
        // FilterWindow's picker) would never update what another open instance (MainWindow's
        // filter bar) displays, even though they share the same vm and SelectedPlayerFilter
        // itself is already correctly in sync (found via direct report: "doesn't sync the
        // fields yet"). Re-subscribes on every DataContext change since MainViewModel is
        // long-lived but individual picker instances (FilterWindow's) come and go.
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel oldVm) oldVm.PropertyChanged -= Vm_PropertyChanged;
            if (e.NewValue is MainViewModel newVm) newVm.PropertyChanged += Vm_PropertyChanged;
            SyncDisplayText();
        };
        Unloaded += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.PropertyChanged -= Vm_PropertyChanged;
        };
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedPlayerFilter)) SyncDisplayText();
    }

    /// <summary>Resyncs the closed box's displayed text from the ViewModel's current
    /// SelectedPlayerFilter - needed after anything that can change that option's DisplayText
    /// out from under an already-set selection (e.g. tagging a face adds a "(tagged)" suffix),
    /// since the box's text is otherwise only set explicitly (see class doc comment), not a
    /// live binding.</summary>
    public void SyncDisplayText()
    {
        if (DataContext is not MainViewModel vm) return;
        SetPlayerFilterText(TextBoxTextFor(vm.SelectedPlayerFilter));
    }

    private void PlayerFilterTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        PlayerFilterListBox.ItemsSource = vm.SearchPlayerFilterOptions(PlayerFilterTextBox.Text);
        PlayerFilterPopup.IsOpen = true;
        PlayerFilterTextBox.SelectAll();
    }

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

    /// <summary>Escape reverts and closes the popup. Up/Down move the highlighted row in
    /// PlayerFilterListBox without moving focus out of the text box - the popup is a separate
    /// hwnd, so it never gets ordinary keyboard focus to do this on its own. Tab/Enter commit
    /// the highlighted row (defaulting to the top match if none is highlighted yet) - the
    /// shell/IDE "type a few letters, Tab to accept the top suggestion" convention (found via
    /// direct request). Tab deliberately isn't marked Handled afterward, so it still moves focus
    /// to the next control as normal once the commit has happened.</summary>
    private void PlayerFilterTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                PlayerFilterPopup.IsOpen = false;
                SetPlayerFilterText(TextBoxTextFor(vm.SelectedPlayerFilter));
                break;

            case Key.Down when PlayerFilterPopup.IsOpen && PlayerFilterListBox.Items.Count > 0:
                e.Handled = true;
                PlayerFilterListBox.SelectedIndex =
                    Math.Min(PlayerFilterListBox.SelectedIndex + 1, PlayerFilterListBox.Items.Count - 1);
                PlayerFilterListBox.ScrollIntoView(PlayerFilterListBox.SelectedItem);
                break;

            case Key.Up when PlayerFilterPopup.IsOpen && PlayerFilterListBox.Items.Count > 0:
                e.Handled = true;
                PlayerFilterListBox.SelectedIndex = Math.Max(PlayerFilterListBox.SelectedIndex - 1, 0);
                PlayerFilterListBox.ScrollIntoView(PlayerFilterListBox.SelectedItem);
                break;

            case Key.Tab or Key.Enter when PlayerFilterPopup.IsOpen && PlayerFilterListBox.Items.Count > 0:
                var option = PlayerFilterListBox.SelectedItem as MainViewModel.PlayerFilterOption
                    ?? (MainViewModel.PlayerFilterOption)PlayerFilterListBox.Items[0]!;
                if (e.Key == Key.Enter) e.Handled = true;
                CommitPlayerFilter(vm, option);
                break;
        }
    }

    private void CommitPlayerFilter(MainViewModel vm, MainViewModel.PlayerFilterOption option)
    {
        vm.SelectedPlayerFilter = option;
        SetPlayerFilterText(TextBoxTextFor(option));
        PlayerFilterPopup.IsOpen = false;
    }

    private void PlayerFilterListBox_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (PlayerFilterListBox.SelectedItem is not MainViewModel.PlayerFilterOption option) return;
        CommitPlayerFilter(vm, option);
    }
}

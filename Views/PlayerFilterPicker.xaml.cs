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
/// SelectedOption, and losing focus without picking anything reverts the box back to whatever
/// option is still actually selected - a plain ComboBox with ~1800 players in alphabetical order
/// was unusable for finding one specific person by name (found via a real report).
///
/// A UserControl rather than something hardcoded to MainViewModel.SelectedPlayerFilter, so it
/// can be reused both as MainWindow's single global filter-bar box AND as one row of
/// FilterWindow's multi-player list (each row bound to its own PlayerFilterRow.Option) -
/// SelectedOption is the two-way-bindable "what this instance currently holds" DP, decoupled
/// from SearchSource (always the shared MainViewModel, needed for SearchPlayerFilterOptions
/// regardless of what SelectedOption is bound to - a FilterWindow row's DataContext is its own
/// PlayerFilterRow, not MainViewModel, so this can't just be read off DataContext there).
/// </summary>
public partial class PlayerFilterPicker : UserControl
{
    private static readonly MainViewModel.PlayerFilterOption DefaultOption = new(null, null, "(all players)");

    private const string DefaultTooltip =
        "Search for a player to filter the photo list.\nAlso matches old names and stylized usernames.\n\n" +
        "'(tagged)' = you've confirmed a face tag for them.\n'(manual)' = added by name, not yet linked to a VRC account.";
    private const string MultiSelectTooltip = "Multiple players filtered - manage from the Filter window (Ctrl+F).";

    public static readonly DependencyProperty SelectedOptionProperty = DependencyProperty.Register(
        nameof(SelectedOption), typeof(MainViewModel.PlayerFilterOption), typeof(PlayerFilterPicker),
        new FrameworkPropertyMetadata(DefaultOption, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => (d as PlayerFilterPicker)?.SyncDisplayText()));

    public MainViewModel.PlayerFilterOption SelectedOption
    {
        get => (MainViewModel.PlayerFilterOption)GetValue(SelectedOptionProperty);
        set => SetValue(SelectedOptionProperty, value);
    }

    /// <summary>Always the shared MainViewModel (for SearchPlayerFilterOptions), regardless of
    /// what SelectedOption is bound to. MainWindow's instance can just bind "{Binding}" (its
    /// own DataContext already is the MainViewModel); FilterWindow's per-row instances need
    /// "{Binding DataContext, RelativeSource={RelativeSource AncestorType=Window}}" since their
    /// own DataContext is a PlayerFilterRow instead.</summary>
    public static readonly DependencyProperty SearchSourceProperty = DependencyProperty.Register(
        nameof(SearchSource), typeof(MainViewModel), typeof(PlayerFilterPicker),
        new PropertyMetadata(null, OnSearchSourceChanged));

    public MainViewModel? SearchSource
    {
        get => (MainViewModel?)GetValue(SearchSourceProperty);
        set => SetValue(SearchSourceProperty, value);
    }

    /// <summary>True only for MainWindow's single global box - it can only ever show/edit one
    /// player, so once SearchSource.PlayerFilterCriteria has 2+ active rows (added via
    /// FilterWindow), it shows a read-only "N players filtered" summary instead of pretending
    /// to reflect just the first one. FilterWindow's own per-row instances leave this false:
    /// each row always shows exactly its own bound option, regardless of how many other rows
    /// exist.</summary>
    public static readonly DependencyProperty CollapseWhenMultipleProperty = DependencyProperty.Register(
        nameof(CollapseWhenMultiple), typeof(bool), typeof(PlayerFilterPicker), new PropertyMetadata(true));

    public bool CollapseWhenMultiple
    {
        get => (bool)GetValue(CollapseWhenMultipleProperty);
        set => SetValue(CollapseWhenMultipleProperty, value);
    }

    public PlayerFilterPicker()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            if (SearchSource is { } vm) vm.PropertyChanged -= Vm_PropertyChanged;
        };
    }

    /// <summary>CollapseWhenMultiple's "N players filtered" summary depends on the whole
    /// PlayerFilterCriteria list's shape, not just this instance's own SelectedOption value -
    /// going from 1 to 2 active rows elsewhere doesn't necessarily change what SelectedOption
    /// itself resolves to (still "the first non-empty row"), so the DP's own change callback
    /// alone can't be trusted to catch it. MainViewModel.OnPlayerFilterCriteriaChanged always
    /// raises PropertyChanged(SelectedPlayerFilter) on every row edit regardless of whether the
    /// value actually changed, so listening for that name directly (rather than relying on
    /// SelectedOption's WPF-level equality-gated callback) is what actually stays correct here.</summary>
    private static void OnSearchSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PlayerFilterPicker picker) return;
        if (e.OldValue is MainViewModel oldVm) oldVm.PropertyChanged -= picker.Vm_PropertyChanged;
        if (e.NewValue is MainViewModel newVm) newVm.PropertyChanged += picker.Vm_PropertyChanged;
        picker.SyncDisplayText();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedPlayerFilter)) SyncDisplayText();
    }

    /// <summary>Resyncs the closed box's displayed text - needed after anything that can change
    /// what should be shown out from under an already-set selection (e.g. tagging a face adds a
    /// "(tagged)" suffix to SelectedOption's DisplayText, or another instance changes the shared
    /// criteria list's shape), since the box's text is otherwise only set explicitly (see class
    /// doc comment), not a live binding.</summary>
    public void SyncDisplayText()
    {
        if (CollapseWhenMultiple && SearchSource is { } vm)
        {
            int activeCount = vm.PlayerFilterCriteria.Count(r => !r.IsEmpty);
            if (activeCount >= 2)
            {
                PlayerFilterTextBox.IsEnabled = false;
                PlayerFilterTextBox.ToolTip = MultiSelectTooltip;
                SetPlayerFilterText($"{activeCount} players filtered");
                return;
            }
        }
        PlayerFilterTextBox.IsEnabled = true;
        PlayerFilterTextBox.ToolTip = DefaultTooltip;
        SetPlayerFilterText(TextBoxTextFor(SelectedOption));
    }

    private void PlayerFilterTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (SearchSource is not { } vm) return;
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
    /// hwnd - gets to run its own MouseUp handler first. By the time this runs, SelectedOption
    /// (and this box's Text) already reflects any new choice, so reapplying it here is a
    /// harmless no-op in that case; it only actually changes anything when the user typed a
    /// search and then clicked away without picking a result, where it correctly reverts the
    /// stray typed text back to the option that's still really selected.
    /// </summary>
    private void PlayerFilterTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            PlayerFilterPopup.IsOpen = false;
            SyncDisplayText();
        });
    }

    private void PlayerFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchSource is not { } vm) return;
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
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                PlayerFilterPopup.IsOpen = false;
                SyncDisplayText();
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
                CommitPlayerFilter(option);
                break;
        }
    }

    private void CommitPlayerFilter(MainViewModel.PlayerFilterOption option)
    {
        SelectedOption = option;
        SyncDisplayText();
        PlayerFilterPopup.IsOpen = false;
    }

    private void PlayerFilterListBox_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (PlayerFilterListBox.SelectedItem is not MainViewModel.PlayerFilterOption option) return;
        CommitPlayerFilter(option);
    }
}

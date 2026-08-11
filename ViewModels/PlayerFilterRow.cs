using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VrcPhotoManager.ViewModels;

/// <summary>One row of FilterWindow's multi-player filter list - either a positive
/// requirement (the photo must include this player) or an exclusion (the photo must NOT
/// include them - e.g. "everyone but me"). MainViewModel.PlayerFilterCriteria holds these; an
/// empty trailing row (Option is the "(all players)" sentinel, IsEmpty true) is always kept at
/// the end so picking a player there automatically reveals a fresh empty row beneath it - see
/// MainViewModel.EnsurePlayerFilterCriteriaShape.</summary>
public class PlayerFilterRow : INotifyPropertyChanged
{
    private MainViewModel.PlayerFilterOption _option;
    public MainViewModel.PlayerFilterOption Option
    {
        get => _option;
        set
        {
            if (_option == value) return;
            _option = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasPlayer));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool _exclude;
    public bool Exclude
    {
        get => _exclude;
        set
        {
            if (_exclude == value) return;
            _exclude = value;
            OnPropertyChanged();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsEmpty => Option.VrcUserId is null && Option.PersonId is null;

    /// <summary>Inverse of IsEmpty - the FilterWindow row template binds this rather than
    /// negating IsEmpty inline, so the remove ("x") button only shows once a row actually has a
    /// player picked (the always-present trailing empty row has nothing meaningful to remove).</summary>
    public bool HasPlayer => !IsEmpty;

    /// <summary>Raised whenever Option or Exclude changes - MainViewModel subscribes once per
    /// row (see EnsurePlayerFilterCriteriaShape) to reshape the list (auto-add/collapse empty
    /// rows) and re-filter the grid.</summary>
    public event EventHandler? Changed;

    public PlayerFilterRow(MainViewModel.PlayerFilterOption option) => _option = option;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

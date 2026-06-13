using System.Collections.ObjectModel;
using LauncherTF2.Core;

namespace LauncherTF2.Models.Inventory;

/// <summary>
/// Multi-select filter state for the inventory grid. Each category holds a set
/// of selected chips; an empty set means "no filter applied" for that category.
/// </summary>
public class InventoryFilterState : ViewModelBase
{
    /// <summary>Fires immediately when any chip or sort mode changes (instant UI update).</summary>
    public event EventHandler? FilterChanged;
    /// <summary>Fires when the search text changes — callers should debounce this one.</summary>
    public event EventHandler? SearchTextChanged;

    private string _searchText = string.Empty;
    private SortMode _sortMode = SortMode.NameAsc;
    private bool _suppressEvents;

    public ObservableCollection<FilterChip> Classes { get; } = new();
    public ObservableCollection<FilterChip> Qualities { get; } = new();
    public ObservableCollection<FilterChip> ItemTypes { get; } = new();
    public ObservableCollection<FilterChip> Rarities { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value) && !_suppressEvents)
                SearchTextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public SortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (SetProperty(ref _sortMode, value))
            {
                OnPropertyChanged(nameof(SortLabel));
                if (!_suppressEvents)
                    FilterChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string SortLabel => _sortMode switch
    {
        SortMode.NameAsc => "Name A→Z",
        SortMode.NameDesc => "Name Z→A",
        SortMode.RarityAsc => "Common first",
        SortMode.RarityDesc => "Rarity first",
        _ => "Sort"
    };

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(_searchText) &&
        !Classes.Any(c => c.IsSelected) &&
        !Qualities.Any(c => c.IsSelected) &&
        !ItemTypes.Any(c => c.IsSelected) &&
        !Rarities.Any(c => c.IsSelected);

    public void Clear()
    {
        _suppressEvents = true;
        try
        {
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            foreach (var c in Classes) c.IsSelected = false;
            foreach (var c in Qualities) c.IsSelected = false;
            foreach (var c in ItemTypes) c.IsSelected = false;
            foreach (var c in Rarities) c.IsSelected = false;
        }
        finally
        {
            _suppressEvents = false;
            FilterChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void NotifyChipChanged()
    {
        if (!_suppressEvents)
            FilterChanged?.Invoke(this, EventArgs.Empty);
    }
}

public enum SortMode
{
    NameAsc,
    NameDesc,
    RarityAsc,
    RarityDesc
}

/// <summary>Single multi-selectable chip in a filter category.</summary>
public class FilterChip : ViewModelBase
{
    private bool _isSelected;

    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? SelectionChanged;
}

using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Eternal.App.ViewModels;

public sealed class BulkCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        if (Items.Count == items.Count && Items.SequenceEqual(items))
            return;
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

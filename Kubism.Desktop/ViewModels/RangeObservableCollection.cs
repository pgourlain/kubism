using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Kubism.Desktop.ViewModels;

/// <summary>ObservableCollection with bulk operations that raise a single Reset instead of one event per item.</summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Makes the collection equal to <paramref name="desired"/> with minimal inserts/moves/removes (keeps selection stable).</summary>
    public void SyncTo(IReadOnlyList<T> desired, int resetThreshold = 300)
    {
        if (Count == desired.Count && this.SequenceEqual(desired)) return;
        var present = new HashSet<T>(this);
        var wanted = new HashSet<T>(desired);
        var removals = present.Count(p => !wanted.Contains(p));
        var additions = desired.Count(d => !present.Contains(d));
        if (removals + additions > resetThreshold) { ReplaceAll(desired); return; }
        for (var i = Count - 1; i >= 0; i--) if (!wanted.Contains(this[i])) RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < Count && EqualityComparer<T>.Default.Equals(this[i], desired[i])) continue;
            var existing = -1;
            for (var j = i + 1; j < Count; j++) if (EqualityComparer<T>.Default.Equals(this[j], desired[i])) { existing = j; break; }
            if (existing >= 0) Move(existing, i); else Insert(i, desired[i]);
        }
        while (Count > desired.Count) RemoveAt(Count - 1);
    }
}

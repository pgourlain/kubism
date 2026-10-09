namespace Kubism.Core.Events;

/// <summary>Hand-rolled observable (no Rx dependency in Core).</summary>
public sealed class EventStream<T> : IObservable<T>
{
    private readonly object _gate = new();
    private List<IObserver<T>> _observers = [];

    public IDisposable Subscribe(IObserver<T> observer)
    {
        lock (_gate) _observers = [.. _observers, observer];
        return new Sub(this, observer);
    }

    public void Push(T item)
    {
        List<IObserver<T>> snapshot;
        lock (_gate) snapshot = _observers;
        foreach (var o in snapshot)
        {
            try { o.OnNext(item); } catch { /* a faulty observer must not break the producer */ }
        }
    }

    private sealed class Sub(EventStream<T> owner, IObserver<T> observer) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) owner._observers = owner._observers.Where(o => !ReferenceEquals(o, observer)).ToList();
        }
    }
}

public static class ObservableExtensions
{
    public static IDisposable Subscribe<T>(this IObservable<T> source, Action<T> onNext) => source.Subscribe(new ActionObserver<T>(onNext));

    private sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => onNext(value);
    }
}

using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Core.Sessions;

namespace Kubism.Core.Events;

public abstract record ClusterEvent;
public sealed record CatalogChanged : ClusterEvent;
public sealed record SessionStateChanged(string ClusterId, SessionState State, string? Error) : ClusterEvent;
public sealed record CacheChanged(string ClusterId, Gvr Gvr, string? Namespace, Cache.CacheChange Change) : ClusterEvent;
public sealed record ActionCompleted(string ClusterId, string Action, string Target) : ClusterEvent;
public sealed record ActionFailed(string ClusterId, string Action, string Target, string Error) : ClusterEvent;
public sealed record PortForwardChanged(string ClusterId, Guid ForwardId) : ClusterEvent;

public interface IClusterEventsService : IObservable<ClusterEvent>
{
    void Push(ClusterEvent evt);
}

public sealed class ClusterEventsService : IClusterEventsService
{
    private readonly EventStream<ClusterEvent> _stream = new();
    public void Push(ClusterEvent evt) => _stream.Push(evt);
    public IDisposable Subscribe(IObserver<ClusterEvent> observer) => _stream.Subscribe(observer);
}

using CommunityToolkit.Mvvm.ComponentModel;
using Kubism.Core.Cache;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

public sealed partial class ResourceRowViewModel(CacheItem item) : ViewModelBase
{
    public CacheItem Item { get; private set; } = item;
    public string Uid => Item.Object.Uid;
    public string Name => Item.Object.Name;
    public string? Namespace => Item.Object.Namespace;

    /// <summary>Hover text per cell, same indexing as <see cref="Cells"/>; null where the cell text says it all. Set before Cells.</summary>
    public string?[] Tips { get; set; } = [];
    [ObservableProperty] private string[] _cells = [];
    [ObservableProperty] private Severity _severity;
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private bool _isDeleting;

    public void Update(CacheItem item) => Item = item;

    public static Severity SeverityOf(string kind, CacheItem item)
    {
        switch (kind)
        {
            case "Pod":
                var s = new PodView(item.Object).Status;
                if (s is "Running" or "Completed" or "Succeeded") return Severity.Ok;
                if (s is "Pending" or "ContainerCreating" or "Terminating" or "PodInitializing" or "NotReady" || s.StartsWith("Init:") && !s.Contains("Error") && !s.Contains("BackOff")) return Severity.Warning;
                return Severity.Error;
            case "Node":
                var n = new NodeView(item.Object).Status;
                return n == "Ready" ? Severity.Ok : n.StartsWith("Ready") ? Severity.Warning : Severity.Error;
            case "Event":
                return new EventView(item.Object).IsWarning ? Severity.Warning : Severity.Neutral;
            case "Deployment" or "StatefulSet" or "ReplicaSet":
                var dv = new DeploymentView(item.Object);
                return dv.Ready >= dv.Desired ? Severity.Ok : Severity.Warning;
            default:
                return Severity.Neutral;
        }
    }
}

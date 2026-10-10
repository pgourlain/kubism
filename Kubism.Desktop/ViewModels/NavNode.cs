using CommunityToolkit.Mvvm.ComponentModel;
using Kubism.Core.Discovery;

namespace Kubism.Desktop.ViewModels;

public enum NavTarget { Group, Overview, Kind, RightSizing, Helm, Activity, CreateYaml, PortForwards, Crds, Compare, Security, GitOps }

/// <summary>A node of the workspace sidebar.</summary>
public sealed partial class NavNode : ViewModelBase
{
    public NavNode(string title, NavTarget target = NavTarget.Group, ResourceKind? kind = null, string? iconKey = null)
    {
        Title = title; Target = target; Kind = kind; IconKey = iconKey;
    }

    public string Title { get; }
    public NavTarget Target { get; }
    public ResourceKind? Kind { get; }
    public string? IconKey { get; }
    public List<NavNode> Children { get; } = [];
    [ObservableProperty] private bool _isDimmed;
    [ObservableProperty] private bool _isExpanded = true;
    public bool IsGroup => Target == NavTarget.Group;
    public string? ShortNames => Kind is { ShortNames.Count: > 0 } ? string.Join(", ", Kind.ShortNames) : null;
    public override string ToString() => Title;
}

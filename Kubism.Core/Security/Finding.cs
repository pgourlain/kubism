namespace Kubism.Core.Security;

public enum FindingSeverity { Low, Medium, High }

/// <param name="Rule">Stable id of the rule that fired ("privileged", "cluster-admin-binding"…), so findings can be grouped and filtered.</param>
/// <param name="Subject">What it is about: "Deployment/default/web", "ClusterRoleBinding/ops".</param>
public sealed record Finding(FindingSeverity Severity, string Rule, string Title, string Detail, string Subject, string Kind, string? Namespace, string Name)
{
    public string Display => Namespace is null ? $"{Kind}/{Name}" : $"{Kind}/{Namespace}/{Name}";
}

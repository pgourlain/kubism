using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kubism.Core.Actions;
using Kubism.Core.Drain;

namespace Kubism.Desktop.ViewModels;

/// <summary>
/// The pre-flight of the Drain page: what would happen to each pod of the chosen nodes. Read-only and pure: changing the nodes or an option re-runs
/// the analysis over what was read, nothing is sent to the cluster. The page's Drain button stays off while some pod would not be migrated,
/// unless the user explicitly chooses to drain anyway.
/// </summary>
public sealed partial class DrainPlanViewModel : ViewModelBase
{
    private DrainSnapshot? _snapshot;
    private IReadOnlyList<string> _nodes = [];

    public DrainPlan Plan { get; private set; } = new([], [], []);
    public ObservableCollection<PodVerdict> Rows { get; } = [];

    [ObservableProperty] private bool _deleteEmptyDirData;
    [ObservableProperty] private bool _drainAnyway;
    /// <summary>All, Problems (blockers and warnings) or Blockers.</summary>
    [ObservableProperty] private string _show = "All";

    public static IReadOnlyList<string> ShowChoices { get; } = ["All", "Problems", "Blockers"];

    public DrainOptions Options => new(DeleteEmptyDirData: DeleteEmptyDirData, Force: DrainAnyway);

    public bool HasNodes => _nodes.Count > 0;
    public bool HasBlockers => Plan.Blockers > 0;
    public bool AllClear => HasNodes && Plan.CanComplete;
    public string Summary => HasNodes ? Plan.Summary : "";
    public IReadOnlyList<string> Notes => Plan.Notes;
    public bool CanDrain => HasNodes && (Plan.CanComplete || DrainAnyway);
    public string Verdict => !HasNodes ? "Tick the nodes to drain, on the left."
        : Plan.CanComplete ? "Every pod can be migrated (predicted)."
        : $"{Plan.Blockers} pod{(Plan.Blockers == 1 ? "" : "s")} would not be migrated: they would be lost or stay Pending. Fix them first, or choose to drain anyway.";

    /// <summary>Gives the plan what the page read and the nodes now chosen, and re-runs the analysis.</summary>
    public void Set(DrainSnapshot? snapshot, IReadOnlyList<string> nodes)
    {
        _snapshot = snapshot; _nodes = nodes;
        Recompute();
    }

    partial void OnDeleteEmptyDirDataChanged(bool value) => Recompute();
    partial void OnDrainAnywayChanged(bool value) => OnPropertyChanged(nameof(CanDrain));
    partial void OnShowChanged(string value) => Refill();

    private void Recompute()
    {
        Plan = _snapshot is null || _nodes.Count == 0 ? new DrainPlan(_nodes, [], _snapshot?.Notes ?? []) : DrainPreflight.Analyze(_snapshot, _nodes, Options);
        if (!HasNodes || Plan.CanComplete) DrainAnyway = false;   // "anyway" only means something while there are blockers
        Refill();
        foreach (var n in new[] { nameof(Plan), nameof(Summary), nameof(Notes), nameof(CanDrain), nameof(Verdict), nameof(HasNodes), nameof(HasBlockers), nameof(AllClear) }) OnPropertyChanged(n);
    }

    private void Refill()
    {
        Rows.Clear();
        foreach (var p in Plan.Pods.Where(p => Show switch { "Blockers" => p.Severity == DrainSeverity.Blocker, "Problems" => p.Severity != DrainSeverity.Ok, _ => true }))
            Rows.Add(p);
    }

    public string ToMarkdown() => DrainPlanExport.ToMarkdown(Plan);
    public string ToCsv() => DrainPlanExport.ToCsv(Plan);
}

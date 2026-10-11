using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kubism.Core;
using Kubism.Core.Api;
using Kubism.Core.Discovery;
using Kubism.Core.Security;
using Kubism.Core.Views;

namespace Kubism.Desktop.ViewModels;

public sealed record FindingRow(Finding Finding)
{
    public string Level => Finding.Severity.ToString();
    public Severity Severity => Finding.Severity switch { FindingSeverity.High => Severity.Error, FindingSeverity.Medium => Severity.Warning, _ => Severity.Neutral };
    public string Display => Finding.Display;
    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
}

public sealed record CertificateRow(CertificateInfo Info)
{
    public Severity Severity => Info.Health switch { CertificateHealth.Expired or CertificateHealth.ExpiresVerySoon => Severity.Error, CertificateHealth.ExpiresSoon => Severity.Warning, _ => Severity.Ok };
    public string Display => Info.Display;
    public string Expires => Info.Expires;
    public string NotAfter => Info.NotAfter.ToString("yyyy-MM-dd");
}

/// <summary>
/// The Security page, read-only: an audit of workloads (privileged, host access, root, capabilities, unpinned images), what RBAC grants
/// (risky bindings and roles, plus the rules a user, group or service account ends up with) and the expiry of TLS certificates.
/// Each tab loads when first shown.
/// </summary>
/// <summary>What the Security page showed when the user left it.</summary>
public sealed record SecurityState(int Tab, bool HighOnly, string SubjectQuery, string? SelectedKey);

public sealed partial class SecurityViewModel : ViewModelBase, IClosable, Navigation.INavigablePage
{
    public const int WorkloadsTab = 0, AccessTab = 1, CertificatesTab = 2;
    private static readonly string[] AuditKinds = ["pods", "deployments", "statefulsets", "daemonsets", "jobs", "cronjobs"];
    private readonly WorkspaceViewModel _ws;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool[] _loaded = new bool[3];
    private List<Finding> _audit = [];
    private RbacAnalysis? _rbac;

    private string? _restoreKey;

    public SecurityViewModel(WorkspaceViewModel ws, SecurityState? restore = null)
    {
        _ws = ws;
        if (restore is not null) { _tab = restore.Tab; _highOnly = restore.HighOnly; _subjectQuery = restore.SubjectQuery; _restoreKey = restore.SelectedKey; }
        _ = EnsureLoadedAsync(Tab);
    }

    public object? CaptureState() => new SecurityState(Tab, HighOnly, SubjectQuery, Selected is { } f ? $"{f.Finding.Rule}|{f.Display}" : null);

    private void RestoreSelection(IEnumerable<FindingRow> rows)
    {
        if (_restoreKey is null) return;
        var key = _restoreKey; _restoreKey = null;
        Selected = rows.FirstOrDefault(r => $"{r.Finding.Rule}|{r.Display}" == key);
    }

    public ObservableCollection<FindingRow> AuditRows { get; } = [];
    public ObservableCollection<FindingRow> AccessRows { get; } = [];
    public ObservableCollection<GrantedRule> SubjectRules { get; } = [];
    public ObservableCollection<CertificateRow> Certificates { get; } = [];
    public ObservableCollection<string> KnownSubjects { get; } = [];
    [ObservableProperty] private int _tab;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _auditSummary = "";
    [ObservableProperty] private string _accessSummary = "";
    [ObservableProperty] private string _certificateSummary = "";
    [ObservableProperty] private bool _highOnly;
    [ObservableProperty] private FindingRow? _selected;
    [ObservableProperty] private string _subjectQuery = "";
    [ObservableProperty] private string _subjectSummary = "";

    partial void OnTabChanged(int value) => _ = EnsureLoadedAsync(value);
    partial void OnHighOnlyChanged(bool value) { ShowAudit(); ShowAccess(); }

    [RelayCommand]
    private Task Refresh() { _loaded[Tab] = false; return EnsureLoadedAsync(Tab); }

    private async Task EnsureLoadedAsync(int tab)
    {
        if (tab is < 0 or > 2 || _loaded[tab]) return;
        _loaded[tab] = true;
        IsLoading = true; Error = null;
        try
        {
            switch (tab)
            {
                case WorkloadsTab: await LoadAuditAsync(); break;
                case AccessTab: await LoadAccessAsync(); break;
                default: await LoadCertificatesAsync(); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; _loaded[tab] = false; }
        finally { IsLoading = false; }
    }

    private IReadOnlyList<string?> Namespaces() => _ws.Namespaces.IsAll ? [null] : _ws.Namespaces.Selected.Cast<string?>().ToList();

    private async Task<List<KubeObject>> ListAsync(string resource, bool useNamespaces = true)
    {
        var kind = _ws.Session.Discovery.FindByName(resource);
        if (kind is null || !kind.CanList) return [];
        var result = new List<KubeObject>();
        foreach (var ns in kind.Namespaced && useNamespaces ? Namespaces() : [null])
        {
            try { result.AddRange(await CompareViewModel.ListAllAsync(_ws, kind, ns, _cts.Token)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* forbidden: that kind is simply not audited */ }
        }
        return result;
    }

    // ---------- workloads ----------

    private async Task LoadAuditAsync()
    {
        var objects = (await Task.WhenAll(AuditKinds.Select(k => ListAsync(k)))).SelectMany(x => x).Where(WorkloadAudit.ShouldAudit).ToList();
        _audit = objects.SelectMany(WorkloadAudit.Check).OrderByDescending(f => f.Severity).ThenBy(f => f.Display, StringComparer.Ordinal).ThenBy(f => f.Title, StringComparer.Ordinal).ToList();
        var flagged = _audit.Select(f => f.Subject).Distinct().Count();
        _ws.Ui.Post(() =>
        {
            AuditSummary = $"{objects.Count} workloads checked · {flagged} with findings · {_audit.Count(f => f.Severity == FindingSeverity.High)} high, {_audit.Count(f => f.Severity == FindingSeverity.Medium)} medium, {_audit.Count(f => f.Severity == FindingSeverity.Low)} low";
            ShowAudit();
        });
    }

    private void ShowAudit()
    {
        AuditRows.Clear();
        foreach (var f in _audit.Where(f => !HighOnly || f.Severity == FindingSeverity.High)) AuditRows.Add(new FindingRow(f));
        if (Tab == WorkloadsTab) RestoreSelection(AuditRows);
    }

    // ---------- access ----------

    private List<Finding> _access = [];

    private async Task LoadAccessAsync()
    {
        var roles = ListAsync("roles"); var clusterRoles = ListAsync("clusterroles", false);
        var bindings = ListAsync("rolebindings"); var clusterBindings = ListAsync("clusterrolebindings", false);
        await Task.WhenAll(roles, clusterRoles, bindings, clusterBindings);
        _rbac = new RbacAnalysis(roles.Result, clusterRoles.Result, bindings.Result, clusterBindings.Result);
        _access = _rbac.Findings().ToList();
        _ws.Ui.Post(() =>
        {
            AccessSummary = _rbac is null ? "" : $"{clusterRoles.Result.Count + roles.Result.Count} roles · {clusterBindings.Result.Count + bindings.Result.Count} bindings · {_access.Count(f => f.Severity == FindingSeverity.High)} high, {_access.Count(f => f.Severity == FindingSeverity.Medium)} medium";
            KnownSubjects.Clear(); foreach (var s in _rbac!.KnownSubjects().Where(s => !s.Contains("system:"))) KnownSubjects.Add(s);
            ShowAccess();
        });
    }

    private void ShowAccess()
    {
        AccessRows.Clear();
        foreach (var f in _access.Where(f => !HighOnly || f.Severity == FindingSeverity.High)) AccessRows.Add(new FindingRow(f));
        if (Tab == AccessTab) RestoreSelection(AccessRows);
    }

    /// <summary>"ServiceAccount ns/name", "User name" or "Group name"; a bare "ns/name" is taken as a service account.</summary>
    [RelayCommand]
    private async Task LookupAsync()
    {
        if (_rbac is null) { await EnsureLoadedAsync(AccessTab); if (_rbac is null) return; }
        SubjectRules.Clear();
        var q = SubjectQuery.Trim();
        if (q.Length == 0) { SubjectSummary = ""; return; }
        var parts = q.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string kind, name; string? ns = null;
        if (parts.Length == 2 && parts[0].ToLowerInvariant() is "serviceaccount" or "sa" or "user" or "group")
        {
            kind = parts[0].ToLowerInvariant() switch { "user" => "User", "group" => "Group", _ => "ServiceAccount" }; name = parts[1];
        }
        else { kind = q.Contains('/') ? "ServiceAccount" : "User"; name = q; }
        if (kind == "ServiceAccount")
        {
            var slash = name.IndexOf('/');
            if (slash < 0) { ns = _ws.Namespaces.Selected.Count == 1 ? _ws.Namespaces.Selected[0] : "default"; }
            else { ns = name[..slash]; name = name[(slash + 1)..]; }
        }
        var rules = _rbac!.RulesFor(kind, name, ns);
        foreach (var r in rules) SubjectRules.Add(r);
        SubjectSummary = rules.Count == 0 ? $"{kind} {(ns is null ? "" : ns + "/")}{name} holds no permission beyond what the API server grants everyone" : $"{kind} {(ns is null ? "" : ns + "/")}{name}: {rules.Count} rule(s) through {rules.Select(r => r.Binding).Distinct().Count()} binding(s)";
    }

    // ---------- certificates ----------

    private async Task LoadCertificatesAsync()
    {
        var found = new List<CertificateInfo>();
        // The secret list is used only to read each public certificate's expiry; nothing of it is kept.
        var secretKind = _ws.Session.Discovery.FindByName("secrets");
        if (secretKind is { CanList: true })
            foreach (var ns in Namespaces())
            {
                try
                {
                    var page = await _ws.Session.Transport.ListAsync(secretKind.Gvr, ns, new ListOptions { Limit = 500, FieldSelector = "type=kubernetes.io/tls" }, _cts.Token);
                    foreach (var item in page.Items)
                        if (CertificateScan.FromTlsSecret(new KubeObject(item)) is { } c) found.Add(c);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* forbidden */ }
            }
        var certKind = _ws.Session.Discovery.Kinds.FirstOrDefault(k => k.Group == "cert-manager.io" && k.Resource == "certificates" && k.CanList);
        if (certKind is not null)
        {
            // A cert-manager Certificate and the secret it fills are one certificate: show the Certificate, which knows its issuer and readiness.
            var managedSecrets = new HashSet<(string?, string)>();
            foreach (var o in await ListAsync("certificates"))
                if (CertificateScan.FromCertManager(o) is { } c)
                {
                    found.Add(c);
                    if (o.StringAt("spec", "secretName") is { } secret) managedSecrets.Add((o.Namespace, secret));
                }
            found.RemoveAll(c => c.Source == "TLS secret" && managedSecrets.Contains((c.Namespace, c.Name)));
        }
        found.Sort((a, b) => a.NotAfter.CompareTo(b.NotAfter));
        _ws.Ui.Post(() =>
        {
            Certificates.Clear(); foreach (var c in found) Certificates.Add(new CertificateRow(c));
            CertificateSummary = found.Count == 0 ? "No TLS secret or cert-manager certificate found in the selected namespaces"
                : $"{found.Count} certificates · {found.Count(c => c.Health == CertificateHealth.Expired)} expired · {found.Count(c => c.Health == CertificateHealth.ExpiresVerySoon)} within {CertificateScan.VerySoonDays} days · {found.Count(c => c.Health == CertificateHealth.ExpiresSoon)} within {CertificateScan.SoonDays} days";
        });
    }

    [RelayCommand]
    private void OpenSelected()
    {
        if (Selected?.Finding is { } f && f.Kind.Length > 0) _ws.OpenGraphNode(new GraphNode(f.Subject, f.Kind, f.Namespace, f.Name));
    }

    public ValueTask DisposeAsync() { _cts.Cancel(); return ValueTask.CompletedTask; }
}

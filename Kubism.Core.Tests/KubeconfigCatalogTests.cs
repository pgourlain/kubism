using Kubism.Core.Catalog;
using Xunit;

namespace Kubism.Core.Tests;

public class KubeconfigCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kubism-cat-" + Guid.NewGuid().ToString("N")[..8]);

    public KubeconfigCatalogTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string name, string yaml) { var p = Path.Combine(_dir, name); File.WriteAllText(p, yaml); return p; }

    private const string Two = """
        apiVersion: v1
        kind: Config
        clusters:
        - name: c1
          cluster: { server: "https://c1.example.com:6443", certificate-authority: ca.crt }
        - name: c2
          cluster: { server: "https://c2.example.com" }
        users:
        - name: u1
          user: { token: abc }
        contexts:
        - name: ctx-a
          context: { cluster: c1, user: u1, namespace: apps }
        - name: ctx-b
          context: { cluster: c2, user: u1 }
        """;

    private KubeconfigCatalog Make(string? env = null, string? home = null, SettingsStore? settings = null) =>
        new(settings ?? new SettingsStore(), null, () => env, home ?? Path.Combine(_dir, "none"), TimeSpan.FromMilliseconds(50), watch: true);

    [Fact]
    public void Reads_all_contexts_of_a_file()
    {
        var f = Write("cfg", Two);
        using var c = Make(f);
        Assert.Equal(["ctx-a", "ctx-b"], c.Entries.Select(e => e.ContextName));
        var a = c.Entries[0];
        Assert.Equal("https://c1.example.com:6443", a.Server);
        Assert.Equal("c1.example.com:6443", a.ServerHost);
        Assert.Equal("apps", a.DefaultNamespace);
        Assert.Equal("u1", a.User);
    }

    [Fact]
    public void Same_context_name_in_two_files_stays_apart()
    {
        var f1 = Write("one", Two); var f2 = Write("two", Two);
        using var c = Make(string.Join(Path.PathSeparator, f1, f2));
        Assert.Equal(4, c.Entries.Count);
        Assert.Equal(4, c.Entries.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void Invalid_file_is_reported_without_breaking_others()
    {
        var good = Write("good", Two); var bad = Write("bad", "this: [is: not\n  valid");
        using var c = Make(string.Join(Path.PathSeparator, bad, good));
        Assert.Equal(2, c.Entries.Count);
        Assert.Single(c.Errors);
        Assert.Equal(bad, c.Errors[0].FilePath);
    }

    [Fact]
    public void Missing_explicit_file_is_an_error_but_missing_home_config_is_not()
    {
        using var c = Make(Path.Combine(_dir, "nope"));
        Assert.Empty(c.Entries);
        Assert.Single(c.Errors);
    }

    [Fact]
    public void Extra_folder_is_scanned_for_files()
    {
        var sub = Path.Combine(_dir, "extra"); Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "a.yaml"), Two);
        var s = new SettingsStore();
        using var c = Make(settings: s);
        c.AddPath(sub);
        Assert.Equal(2, c.Entries.Count);
    }

    [Fact]
    public void Remove_drops_an_added_file_without_deleting_it()
    {
        var f = Write("added", Two);
        var s = new SettingsStore();
        using var c = Make(settings: s);
        c.AddPath(f);
        Assert.Equal(RemoveResult.RemovedFile, c.Remove(c.Entries[0]));
        Assert.Empty(c.Entries);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Remove_hides_a_context_from_KUBECONFIG_and_restore_brings_it_back()
    {
        var f = Write("cfg", Two);
        var s = new SettingsStore();
        using var c = Make(f, settings: s);
        Assert.Equal(RemoveResult.Hidden, c.Remove(c.Entries[0]));
        Assert.Single(c.Entries);
        c.RestoreHidden();
        Assert.Equal(2, c.Entries.Count);
    }

    [Fact]
    public async Task File_change_raises_CatalogChanged_after_debounce()
    {
        var f = Write("cfg", Two);
        using var c = Make(f);
        var changed = new TaskCompletionSource();
        c.CatalogChanged += () => { if (c.Entries.Count == 3) changed.TrySetResult(); };
        await Task.Delay(300, TestContext.Current.CancellationToken);
        File.WriteAllText(f, Two + "\n- name: ctx-c\n  context: { cluster: c1, user: u1 }\n");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(3, c.Entries.Count);
    }

    [Fact]
    public void Context_matching_prod_regex_starts_as_production()
    {
        var s = new SettingsStore();
        Assert.Equal(SafetyMode.Production, s.GetMetadata("/x/cfg|aks-prod-eu").SafetyMode);
        Assert.Equal(SafetyMode.Normal, s.GetMetadata("/x/cfg|kind-dev").SafetyMode);
    }

    [Fact]
    public void Settings_roundtrip_to_disk_and_never_touch_kubeconfig()
    {
        var f = Write("cfg", Two); var before = File.ReadAllText(f);
        var sp = Path.Combine(_dir, "settings.json");
        var s = new SettingsStore(sp);
        s.SetMetadata("id1", new ClusterMetadata { Label = "Prod EU", Color = "#ff0000", SafetyMode = SafetyMode.ReadOnly, Tags = ["production"] });
        var s2 = new SettingsStore(sp);
        Assert.Equal("Prod EU", s2.GetMetadata("id1").Label);
        Assert.Equal(SafetyMode.ReadOnly, s2.GetMetadata("id1").SafetyMode);
        Assert.Equal(before, File.ReadAllText(f));
        Assert.Contains("\"readOnly\"", File.ReadAllText(sp), StringComparison.OrdinalIgnoreCase);
    }
}

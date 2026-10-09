using Kubism.Core.Api;
using Kubism.Core.Catalog;
using Kubism.Kube;

// Spike: Native AOT + KubernetesClient. Loads a kubeconfig (exec plugin supported), lists pods as raw JSON,
// watches for a few seconds and opens an exec WebSocket. Usage: aot-client <kubeconfig> [watchSeconds]
var path = args.Length > 0 ? args[0] : throw new ArgumentException("kubeconfig path required");
var seconds = args.Length > 1 ? int.Parse(args[1]) : 30;
var entry = KubeconfigCatalog.ParseFile(path)[0];
using var t = new KubeTransport(entry);
var version = await t.GetAsync("/version", default);
Console.WriteLine($"version: {version?["gitVersion"]}");
var list = await t.ListAsync(Gvr.Pods, null, new ListOptions { Limit = 5, AsTable = true }, default);
Console.WriteLine($"pods: {list.Items.Count} (columns: {list.Columns?.Count})");
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
var events = 0;
try { await foreach (var e in t.WatchAsync(Gvr.Pods, null, list.ResourceVersion, false, cts.Token)) events++; } catch (OperationCanceledException) { }
Console.WriteLine($"watch events: {events}");
var pod = list.Items[0]["metadata"]!["name"]!.ToString();
var ns = list.Items[0]["metadata"]!["namespace"]!.ToString();
await using var sock = await t.OpenSocketAsync($"/api/v1/namespaces/{ns}/pods/{pod}/exec?command=sh&stdin=true&stdout=true&tty=true", ["v5.channel.k8s.io", "v4.channel.k8s.io"], default);
var first = await sock.ReceiveAsync(default);
Console.WriteLine($"exec socket: protocol {sock.SubProtocol}, first frame {first?.Length} bytes");
Console.WriteLine("OK");

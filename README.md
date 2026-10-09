# Kubism

**Kub**ernetes **I**nterface for **S**upervising **M**ulti-clusters. *Inspect. Steer. Monitor.*

A cross-platform (macOS, Linux, Windows) desktop app to explore and operate one or several Kubernetes clusters at the same time, in the spirit of Lens/Freelens. Avalonia 12 on .NET 10. Design and rules: [CLAUDE.md](CLAUDE.md). Roadmap and status: [tasks.md](tasks.md).

- Cluster catalog from kubeconfig files (merged `KUBECONFIG`, `~/.kube/config`, extra files/folders); several clusters connected side by side; production / read-only safety modes.
- Live resource lists (list → watch → relist informers), details, YAML view/edit with conflict handling, logs, shell, port-forward, Helm releases, CRD browser.
- Right-sizing: requests/limits vs real usage (Prometheus, Azure Monitor managed Prometheus, VPA or local sampling), verdicts, waste, and ready-to-paste corrections.
- `Kubism --mcp`: an MCP server for AI assistants (read tools by default, [docs/mcp.md](docs/mcp.md)).

```bash
dotnet build Kubism.slnx
dotnet test --project Kubism.Core.Tests     # also: Kube.Tests, Desktop.Tests, Mcp.Tests
dotnet run --project Kubism.Desktop -- --demo   # fake API server + Prometheus, no cluster needed
dotnet run --project Kubism.Desktop             # real clusters from your kubeconfig
packaging/package.sh osx-arm64 0.1.0            # self-contained package into artifacts/
```

Nothing writes to a cluster except through `ClusterActions`; Secret values are never shown unless one key is revealed explicitly, and the MCP server never returns them. Kubism does not need `kubectl`.

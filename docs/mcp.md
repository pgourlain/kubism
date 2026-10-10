# Kubism MCP server

`Kubism --mcp` runs a headless [Model Context Protocol](https://modelcontextprotocol.io) server over **stdio**. It reuses the desktop app's kubeconfig catalog and cluster metadata (labels, safety modes, Prometheus URL), so a cluster marked *production* or *read-only* in the app is treated the same way here.

Nothing is written to stdout except protocol messages; logs go to stderr and `mcp-log.txt` in the data folder.

## Setup

Build once (`dotnet build Kubism.slnx`), then register the executable.

**Claude Code**

```bash
claude mcp add kubism -- dotnet /path/to/Kubism.Desktop/bin/Debug/net10.0/Kubism.dll --mcp
# with changes allowed on non-production clusters:
claude mcp add kubism-rw -- dotnet /path/to/Kubism.dll --mcp --allow-writes
```

**Claude Desktop** (`claude_desktop_config.json`)

```json
{ "mcpServers": { "kubism": { "command": "dotnet", "args": ["/path/to/Kubism.dll", "--mcp"] } } }
```

A packaged release works the same way: `"command": "/Applications/Kubism.app/Contents/MacOS/Kubism", "args": ["--mcp"]`.

Options: `--allow-writes`, `--allow-production`, `--data <folder>` (settings and logs). The server resolves `PATH` from the login shell, so exec auth plugins (`kubelogin`, `aws`, `gke-gcloud-auth-plugin`) are found even when started by a GUI.

## Tools

Read tools (always on). Every `cluster` argument accepts the context name, the label set in the app, or the catalog id.

| Tool | What it returns |
|---|---|
| `list_clusters` | Known clusters with safety mode and connection state |
| `list_kinds` | Kinds served by a cluster (CRDs included), short names, scope |
| `list_resources` | A kubectl-style table (server-side columns), label/field selectors, row limit |
| `get_resource` | One object as YAML, **secret values redacted**, `managedFields` removed |
| `get_events` | Recent events, optionally for one object |
| `get_logs` | Pod logs, bounded (64 KiB, ≤ 2000 lines), `previous` for crashed containers |
| `top_pods`, `top_nodes` | metrics-server usage |
| `describe` | Object + owner chain + events (like `kubectl describe`) |
| `rightsizing_report` | Requests/limits vs usage per workload container, verdicts, recommendations and a YAML snippet |
| `rbac_who_can` | Who can `<verb>` `<resource>` in `<namespace>`: subjects with the binding → role → rule paths and risk flags (RBAC objects only; says when a kind could not be read) |
| `rbac_rights_of` | What a subject can do: paths per namespace through its bindings and implicit groups, risk flags, and a verbs × resources matrix for one namespace |
| `network_exposure` | How traffic reaches the pods: exposure (outside the cluster or not) → Ingress → Service → workload with hosts, paths, ports and problems; namespace, exposure and problems filters (the controller of another namespace stays as context) |

Resources: `kubism://clusters`, `kubism://clusters/{id}/kinds`.

Write tools (only registered with `--allow-writes`): `apply`, `delete`, `scale`, `rollout_restart`. They call `ClusterActions` like the UI does.

## Safety

* **Secrets never leave.** `Secret.data`/`stringData` values and the `last-applied-configuration` annotation are masked at the tool layer (`SecretMasker`); there is no reveal tool. A test drives every tool against a Secret and checks no value appears.
* **Read-only clusters** refuse every write, even with both flags.
* **Production clusters** need `--allow-production` *and* the tool's `confirmName` argument equal to the object name for `delete` and for scaling to zero, the same rule as the typed-name dialog in the UI.
* Writes are recorded in the session's activity log.
* No `kubectl` is used; the server talks to the API directly with the credentials of the kubeconfig.

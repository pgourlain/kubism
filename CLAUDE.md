# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) and other coding agents working in this repository.

> Name: **Kubism**, **Kub**ernetes **I**nterface for **S**upervising **M**ulti-clusters. Tagline: *Inspect. Steer. Monitor.* (also a nod to cubism: see your clusters from every angle, and a sibling of CinnabarSharp).

## Goal

Build a cross-platform desktop app to explore and operate **one or several Kubernetes clusters at the same time**, with the scope and UX of [Lens](https://k8slens.dev/) / [Freelens](https://github.com/freelensapp/freelens): a cluster catalog built from kubeconfig files, live resource lists that update through watches, details and YAML editing, logs, a shell into containers, port-forwarding, metrics, Helm releases and custom resources. When deciding how a feature should behave, use Lens/Freelens as the reference for UX and `kubectl` as the reference for semantics (what a command does to the cluster, which columns a list shows).

The app must run on **macOS, Linux and Windows**. Same stack as [CinnabarSharp](https://github.com/pgourlain/CinnabarSharp): **Avalonia UI 12 on .NET 10**, MVVM with CommunityToolkit.Mvvm, DI with `Microsoft.Extensions.*`, xunit v3, headless UI tests, Native AOT release packages. The phased plan, with a validation checklist per phase, is in [tasks.md](tasks.md).

Primary real-world targets: **AKS with Entra ID** (`kubelogin` exec plugin), plus kind/k3d/minikube locally, EKS/GKE through their exec plugins, and plain certificate/token kubeconfigs.

## Non-negotiable rules

1. **Core is non-visual.** `Kubism.Core` contains no UI code (see below). Enforced by `CoreArchitectureTests`.
2. **Nothing writes to a cluster except through `ClusterActions`.** Every mutating call (apply, delete, scale, patch, exec, port-forward, drain…) goes through one class that checks the cluster's safety mode, records an entry in the session's activity log and raises an event. The UI, the MCP server and the tests all call it. `ClusterActionsTests.Actions` must list every action.
3. **Read-only by default for unknown clusters' destructive actions.** A cluster tagged *production* asks for the resource name to be typed before delete/drain/scale-to-zero; a cluster in *read-only* mode refuses every mutation in `ClusterActions` (not only in the UI).
4. **Secrets are masked.** `Secret.data` values are never shown, logged, sent to telemetry or written in test snapshots unless the user reveals one key explicitly; the MCP server never returns them.
5. **Never block the UI thread on the network.** All API calls are async with a `CancellationToken` owned by the view or session that started them. Closing a view or disconnecting a cluster cancels everything it started.
6. **No `kubectl` dependency.** The app talks to the API server itself. External binaries are only spawned for kubeconfig exec auth plugins (`kubelogin`, `aws`, `gke-gcloud-auth-plugin`) and, optionally, `helm` for write operations on releases.

## Constraint: Kubism.Core is non-visual

`Kubism.Core` contains the cluster/session model, kubeconfig handling, API discovery, the resource cache (informers), the dynamic resource model, table/column model, actions, log/exec/port-forward session logic, metrics aggregation, safety policy and events. It must build and pass its tests on the 3 OSes without any UI framework.

- **Forbidden in Core:** any reference to Avalonia or another UI/windowing stack, SkiaSharp, `System.Drawing`, dispatchers/UI threading, dialogs, clipboard, platform APIs (`#if WINDOWS`, P/Invoke).
- **Allowed:** BCL, `Microsoft.Extensions.*` abstractions (DI, logging, options), `YamlDotNet`, `System.Text.Json` (source-generated contexts only, see AOT).
- **Core does not reference `KubernetesClient` directly.** It talks to clusters through `IKubeTransport` (defined in Core, implemented in `Kubism.Kube`). This keeps Core testable with an in-memory fake and isolates the client library choice (see the AOT spike in Phase 0).
- Anything visual or platform-specific (clipboard, file pickers, opening a browser, OS keychain) lives in Desktop behind an interface defined in Core and is registered through DI.
- Core talks to the UI only through its event streams (`IClusterEventsService`) and interfaces; it never calls the UI.

## Projects

- `Kubism.Core/`: net10.0 class library, all non-visual logic (see above).
- `Kubism.Kube/`: net10.0, the `IKubeTransport` implementation, built on the official C# client [`KubernetesClient`](https://github.com/kubernetes-client/csharp) for kubeconfig parsing, auth (certificates, tokens, exec plugins, OIDC) and the WebSocket exec/port-forward streams. List, watch and get go through the raw HTTP client of the configured `Kubernetes` instance as **JSON nodes**, not typed models (see "Dynamic-first resources"). No UI.
- `Kubism.Mcp/`: MCP server library (official C# SDK `ModelContextProtocol`), Core only, no UI. Hosted by the desktop executable with `Kubism --mcp`.
- `Kubism.Desktop/`: Avalonia 12 desktop app (MVVM with CommunityToolkit.Mvvm). Executable/assembly name: `Kubism`.
- `Kubism.Testing/`: shared test helpers, notably `FakeApiServer`, an in-process HTTP server (Kestrel on `127.0.0.1:0`) that serves discovery, list (JSON and `as=Table`), watch streams (chunked, with the ability to inject `ERROR 410`, bookmarks and disconnects), get/put/patch/delete, and logs for a set of in-memory objects. Every non-integration test runs against it.
- `Kubism.Core.Tests/`: xunit v3, Core unit tests with an in-memory `FakeKubeTransport`, plus `CoreArchitectureTests`.
- `Kubism.Kube.Tests/`: xunit v3, the transport against `FakeApiServer` (auth headers, watch reconnects, Table negotiation, errors).
- `Kubism.Desktop.Tests/`: headless UI tests (Avalonia.Headless + Skia, xunit v3) that render the real `MainWindow` against `FakeApiServer`, assert on the visual tree and pixels, and save screenshots to `bin/<Config>/net10.0/screenshots/`.
- `Kubism.Mcp.Tests/`: start the real `Kubism --mcp` and drive it over stdio with an MCP client, against `FakeApiServer`.
- `Kubism.Integration.Tests/`: xunit v3, against a real cluster. Skipped unless `KUBISM_IT_KUBECONFIG` is set. CI runs them on Linux with a kind cluster.

Root solution: `Kubism.slnx`. Central package versions in `Directory.Packages.props`. CI: `.github/workflows/ci.yml` (build + test on windows-latest, macos-latest, ubuntu-latest; integration tests on ubuntu with kind; UI screenshots uploaded as `screenshots-<os>`). Releases: push a `v*` tag, `.github/workflows/release.yml` packages every OS.

## Commands

```bash
dotnet build Kubism.slnx
dotnet test Kubism.slnx

# Single test
dotnet test Kubism.Core.Tests --filter "FullyQualifiedName~ResourceCacheTests.Relists_after_410_gone"

# Run the app
dotnet run --project Kubism.Desktop

# Run the app against the fake API server (no cluster needed; writes a temp kubeconfig)
dotnet run --project Kubism.Desktop -- --demo

# Integration tests against a local kind cluster
kind create cluster --name kubism-it
KUBISM_IT_KUBECONFIG=~/.kube/config dotnet test Kubism.Integration.Tests

# MCP server over stdio (build first; `dotnet run` writes build output to stdout)
dotnet Kubism.Desktop/bin/Debug/net10.0/Kubism.dll --mcp

# Self-contained package (win-x64 | linux-x64 | osx-arm64 | osx-x64) into artifacts/
packaging/package.sh osx-arm64 0.1.0
```

To check UI changes visually without a display, run `Kubism.Desktop.Tests` and look at the screenshots.

## Architecture

### Overview

```
┌────────────────────────── Kubism.Desktop (Avalonia, MVVM) ──────────────────────────┐
│ MainWindow: ClusterCatalog (left) │ Workspace tabs (one per connected cluster)          │
│   Sidebar nav (kinds) · ResourceListView · DetailsPanel · Dock: Logs / Terminal / PF    │
│ ViewModels subscribe to Core event streams; batch updates onto the UI thread            │
└───────────────▲─────────────────────────────────────────────────────────────────────────┘
                │ interfaces + IObservable events (no UI types)
┌───────────────┴────────────────────── Kubism.Core ──────────────────────────────────┐
│ KubeconfigCatalog → ClusterSession (one per connected context)                          │
│   ├ ApiDiscovery (groups, kinds, verbs, namespaced?)                                    │
│   ├ ResourceCache (ref-counted informers per GVR+namespace: list → watch → relist)      │
│   ├ TableService (server-side Table columns, fallback columns)                          │
│   ├ ClusterActions (the only mutation path; safety policy; activity log)                │
│   ├ LogSessions · ExecSessions · PortForwardManager · MetricsService · HelmReleases     │
│   └ IClusterEventsService (hand-rolled observable streams)                              │
└───────────────▲─────────────────────────────────────────────────────────────────────────┘
                │ IKubeTransport (JSON nodes, streams)
┌───────────────┴───────── Kubism.Kube (KubernetesClient) ────────────────────────────┐
│ KubeConfig loading, auth (certs, tokens, exec plugins, refresh) · raw HTTP list/watch    │
│ · WebSocket exec / attach / port-forward (v5 channel protocol) · logs streaming          │
└──────────────────────────────────────────────────────────────────────────────────────────┘
        Kubism.Mcp (Core only) is hosted by the same executable with --mcp
```

**DI entry point:** `ServiceExtensions.AddKubismCore()` (Core) and `AddKubismKube()` (Kube). `AppServices.Build()` in Desktop is the composition root used by `App` and the UI tests. Singletons: `IKubeconfigCatalog`, `IClusterSessionManager`, `IClusterEventsService`, `ISettingsStore`. Per session (created by `ClusterSessionManager`, disposed on disconnect): `ClusterSession` and everything it owns.

### Kubeconfig catalog

`KubeconfigCatalog` builds the list of clusters from: the `KUBECONFIG` variable (merged in order, as kubectl does), `~/.kube/config`, and extra files/folders the user adds (each file can hold several contexts). Each entry is a `ClusterEntry` (stable id = file path + context name, display name, cluster server URL, user, default namespace, plus user metadata: label, color, icon, tags such as *production*, safety mode). User metadata is stored in the app's settings, **never written back into kubeconfig files**. The catalog watches the files (`FileSystemWatcher`, debounced) and raises `CatalogChanged`.

### Cluster sessions (multi-cluster)

`ClusterSessionManager` holds one `ClusterSession` per connected cluster; several can be connected at once. A session owns its `IKubeConnection` (HTTP client, credentials), discovery, cache, log/exec/port-forward sessions and a root `CancellationTokenSource`; `DisconnectAsync` cancels and disposes all of it. Session state machine: `Disconnected → Connecting → Connected ⇄ Degraded → Disconnected`, with `Error(reason)` (auth failed, unreachable, certificate…). Health: `GET /version` at connect, then `/readyz` every 30 s while connected; a failing watch reconnect also moves the session to `Degraded`. Reconnect uses exponential backoff with jitter.

### Auth

**TLS and the handler chain are ours** (`KubeTls`, `KubeTransport`): `KubernetesClient` is only used as a data holder for the parsed kubeconfig. The library applies tokens only to its own typed calls (never to the raw HTTP client Kubism uses), and leaves the client-certificate choice to the OS: on Windows, SChannel filters by the issuers the server lists, and a kubeconfig's client certificate is issued by the cluster's own CA (in no system store), so it was silently not sent and every request got 401. Now the certificate is always offered (`LocalCertificateSelectionCallback`, PKCS#12 round trip on Windows), a static token is sent with every request next to the certificate (as kubectl does), basic auth is supported, and WebSockets (exec, port-forward) go through the same HTTP client. `MutualTlsTests` runs an HTTPS server that requires a client certificate issued by a private CA.

Delegate to `KubernetesClient`'s `KubernetesClientConfiguration.BuildConfigFromConfigFile(...)` (certs, bearer tokens, token files, exec plugins, OIDC). Known pitfalls to handle explicitly:
- **PATH of GUI apps on macOS (and some Linux desktops):** an app launched from Finder/Dock does not inherit the shell's `PATH`, so `kubelogin`/`aws` are not found. At startup, resolve the login shell's `PATH` once (`$SHELL -l -c 'echo $PATH'`, with a timeout) and use it for exec plugins; show the binary that was not found in the error.
- **Exec credential expiry:** credentials returned with `expirationTimestamp` must be refreshed before they expire and on any `401`. Verify the client library does it for long-lived watches; if not, wrap it in our own refreshing `ServiceClientCredentials`.
- **Interactive exec plugins** (device-code login with `kubelogin`): capture the plugin's stderr and show it in a dialog (the device code and URL) instead of hanging.

### Dynamic-first resources

Everything in the browser is handled as **untyped JSON** (`KubeObject`, a thin wrapper over `JsonObject` with `ApiVersion`, `Kind`, `Metadata` (name, namespace, uid, resourceVersion, labels, annotations, ownerReferences, creationTimestamp, deletionTimestamp) accessors). This is what makes CRDs work exactly like built-in kinds, and it is AOT-safe (no reflection). Typed helpers exist only where a feature needs structure: `PodView` (containers, statuses, restarts, phase/reason as kubectl computes it), `NodeView`, `DeploymentView`, `EventView`… computed from the JSON, unit-tested against kubectl's output.

- **Discovery** (`ApiDiscovery`): aggregated discovery (`/api`, `/apis` with `Accept: application/json;g=apidiscovery.k8s.io;v=v2;as=APIGroupDiscoveryList`) with fallback to legacy per-group discovery. Produces `ResourceKind` (group, version, resource, kind, namespaced, verbs, shortNames, categories). Refreshed on connect and when a CRD watch sees a change.
- **Lists use the server-side Table format** (`Accept: application/json;as=Table;v=v1;g=meta.k8s.io`, `includeObject=Object`): the API server returns the same columns as `kubectl get` for every kind, CRDs included (their `additionalPrinterColumns`). `TableService` keeps the column definitions per kind; the UI adds Lens-style computed columns for common kinds (pods: restarts, CPU/memory from metrics). Fallback when the Table format is refused: Name, Namespace, Age.
- **Pagination:** initial lists use `limit=500` + `continue`.

### Resource cache (informers)

`ResourceCache` keeps **one informer per (cluster, GVR, namespace-or-all)**, created on demand and **reference-counted**: a list view subscribes when shown and releases when hidden; an informer with no subscriber stops after 60 s. An informer does `LIST` (records `resourceVersion`), then `WATCH` with `allowWatchBookmarks=true` from that version, applies `ADDED/MODIFIED/DELETED/BOOKMARK` to an in-memory store keyed by uid, and:
- on `410 Gone` / `ERROR` with code 410: relists and emits a `Resynced` diff (not a burst of adds);
- on stream end or network error: re-watches from the last bookmark with backoff;
- exposes `IObservable<CacheChange>` and a snapshot; changes are **coalesced** (at most one batch per 200 ms per informer) so a busy cluster doesn't flood the UI.

Watches are the transport's job (chunked HTTP, newline-delimited JSON, parsed with `Utf8JsonReader` incrementally). Do not use the client library's typed `Watch<T>` (it doesn't fit the dynamic model, and its AOT support has been limited).

### Actions (the only mutation path)

`ClusterActions` (one per session): `ApplyYaml` (server-side apply with `fieldManager=kubism`, or `PUT` with the read `resourceVersion` for the "edit" flow; a `409 Conflict` returns the fresh object and a diff to the editor, never overwrites), `Delete` (propagation policy option), `Scale` (`/scale` subresource, JSON merge patch), `RolloutRestart` (patch `spec.template.metadata.annotations["kubectl.kubernetes.io/restartedAt"]`, like kubectl), `Cordon/Uncordon` (patch `spec.unschedulable`), `Drain` (cordon + evictions through the `eviction` subresource, respecting PDBs, with progress and cancel), `TriggerCronJob` (create a Job from the template), `StartExec`, `StartPortForward`, `RollbackHelmRelease`… Each action:
1. checks `SafetyPolicy` (read-only refuses; *production* requires the confirmation token the UI got from the typed name),
2. runs with the caller's `CancellationToken`,
3. appends to the session's `ActivityLog` (time, action, target, result; no secret values),
4. raises `ActionCompleted`/`ActionFailed`.
API errors become `KubeApiException` (status code, `Status.reason`, `message`) shown verbatim to the user, because the API server's message is usually the useful part (admission webhook denials, RBAC "forbidden").

**RBAC awareness:** before showing a mutating button, the view model asks `AccessReview` (`SelfSubjectAccessReview`, cached per session for 5 min) and disables the button with a tooltip ("forbidden: you cannot delete pods in namespace x") instead of failing on click. Kinds the user cannot `list` are hidden from the sidebar (or shown dimmed, setting).

### Graph, compare, search

All three are pure Core logic over objects already loaded, unit-tested without a UI (`ExplorationTests`):
- `RelationGraph` (`Core/Views`): the neighbourhood of one object (owners/owned, Service → Pod by selector, Ingress → Service, workload → ConfigMap/Secret/PVC/ServiceAccount, PVC → PV, Pod → Node, HPA → target) with a layered layout and per-node health. Config kinds are leaves (not expanded) unless they are the root; Secret `data` is stripped before the graph is built. Drawn by `RelationGraphControl` in the details panel's Graph tab.
- `ClusterCompare` + `LineDiff`: compares the same kind between two clusters or namespaces after dropping server-generated fields; **Secret values never reach a diff** (a key is masked, and marked `A`/`B` when it differs).
- `EventTimeline`: one time axis for a workload or pod (events, rollouts per replica set revision, pod creations, last container termination with OOM kills, failing conditions).
- `WorkloadAudit`, `RbacAnalysis`, `CertificateScan` (`Core/Security`): the read-only Security page. Pod-spec hardening rules; risky RBAC grants and the rules a subject holds; certificate expiry from TLS secrets (only `tls.crt` is read, never the key) and cert-manager `Certificate` status.
- `GitOpsApp` (`Core/Delivery`): Argo CD Applications and Flux resources reduced to sync/health/level/source/revision/resources; actions `ArgoSync`, `ArgoRefresh`, `FluxReconcile`, `FluxSuspend` live in `ClusterActions`. The GitOps page appears only where those CRDs are installed.
- `CapacityAnalysis` (`Core/Capacity`): node requests against allocatable, free room and fragmentation, a verdict for each pending pod, cost by namespace or label, and the right-sizing what-if. Based on requests, not usage. `RightSizingRunner` (Desktop) builds the right-sizing report for both the dashboard and the what-if.
- `RbacGraph` + `RbacChain` + `RbacStore` + `RbacVerifier` (`Core/Security`): the RBAC page (Access Control › RBAC graph) and the MCP tools `rbac_who_can` / `rbac_rights_of`. Exact authorizer semantics (wildcards, subresources, resourceNames, nonResourceURLs, aggregated roles, implicit groups, RoleBinding → ClusterRole scoped to its namespace), kept live from `ResourceCache`, checked against `SelfSubjectAccessReview`; says when an RBAC kind could not be read. Limits (webhook / Azure RBAC / ABAC): docs/adr/0004-rbac-graph.md.
- `NetworkGraph` + `NetworkStore` + `NetworkLoader` (`Core/Network`): the Network graph page (Network › Network graph) and the MCP tool `network_exposure`. Exposure derived from cluster objects (Service type, ingress controller's Service, external controllers, hostPort), ports resolved hop by hop, problems placed where the fault is, namespace filter that keeps the controller of another namespace as context; TLS secrets looked up by `GET` (existence and expiry only). Limits (DNS, firewalls, WAF, NetworkPolicy and Gateway API not yet read): docs/adr/0005-network-graph.md. `ColumnLayout` (`Core/Util`) is the layout shared with `RbacChain`.
- `ResourceUsage` (`Core/Usage`): the Usage pages (Config, Storage, Network and Access Control › Usage). Which ConfigMaps, Secrets, PVCs, PVs, StorageClasses, ServiceAccounts and IngressClasses anything refers to (mounts, env, envFrom, imagePullSecrets, projected, Ingress TLS, ServiceAccount, StatefulSet claim templates, default class), who, and which references point at nothing (`Broken`). Status: *Used*, *Not referenced* (never "useless": operators or scripts outside the objects read can still use it), *Managed* (Helm data, SA tokens, owned objects). Only names are read; Secret `data` is dropped as soon as the list arrives. Controllers count through their pod template and pods they own are skipped. ConfigMap keys nobody reads (`DeadKeys`) are reported only when every consumer names its keys (`configMapKeyRef`, volume `items`, subPath mounts); `envFrom` or a whole-map volume reads everything. `DanglingCheck` adds HPAs whose target is gone, at zero replicas or shared, and PDBs that select no pod or forbid every eviction (the "HPA & PDB" section). A pod's ServiceAccount (unset = `default`) and an Ingress's class and backend Services are references too; a missing one is a broken reference. `ResourceUsageTests`.
- `SearchQuery`: global search syntax (`text`, `key=value`, `image:`, `ns:`, `kind:`, AND).

### Logs, exec, port-forward

- **Logs** (`LogSession`): `GET …/pods/{name}/log?follow=true&timestamps=true&container=…&tailLines=…&previous=…&sinceSeconds=…`, streamed into a bounded ring buffer (default 50 000 lines, setting) so memory is capped. Multi-pod logs for a workload (deployment/statefulset/job) = one stream per pod, merged by timestamp, with a pod/container prefix. Search, wrap, timestamps on/off, pause (buffering continues), download to a file.
- **Exec** (`ExecSession`): WebSocket exec through the client library (`v5.channel.k8s.io`, falling back to `v4`), with TTY, stdin, resize channel (4) wired to the terminal size, and shell detection (`/bin/bash`, falls back to `/bin/sh`). Also "attach" and node shell later. The terminal emulator control lives in Desktop (see UI).
- **Port-forward** (`PortForwardManager`): local `TcpListener` on `127.0.0.1` (port chosen or auto), one WebSocket port-forward stream per accepted connection, to a pod port (or a service: resolve the service's selector to a ready pod and its `targetPort`). List of active forwards per cluster with stop/open-in-browser; they stop when the cluster disconnects; they are not restored at startup unless the user marks them "keep".

### Metrics

`MetricsService` polls `metrics.k8s.io/v1beta1` (nodes and pods) every 15 s while a view needs it (ref-counted like informers), keeps a short history (15 min) per object for sparklines, and reports "metrics-server not installed" once instead of erroring. Prometheus as a second source is a later phase (`IMetricsSource`).

### Right-sizing (requests/limits vs real usage)

Goal: for every container of every workload, put **requests and limits next to what the container really uses over days**, flag what is over- or under-provisioned, and produce the corrected `resources` block. Prior art to compare against: Robusta KRR (Prometheus, p95 CPU / max memory) and Fairwinds Goldilocks (VPA recommendations).

**History is the hard part.** metrics-server only gives the current value, and the app's own 15 min buffer is far too short to size anything. So right-sizing reads history from a `IUsageHistorySource`, in this order of preference:
1. **Prometheus** (cAdvisor metrics): `container_cpu_usage_seconds_total` (rate), `container_memory_working_set_bytes`, `container_cpu_cfs_throttled_periods_total` / `container_cpu_cfs_periods_total`, `kube_pod_container_status_last_terminated_reason` (kube-state-metrics, OOMKilled). Reached by URL, or through the API server service proxy for an in-cluster Prometheus.
2. **Azure Monitor managed Prometheus** (AKS): same PromQL against the Azure Monitor workspace query endpoint, with an Entra token for scope `https://prometheus.monitor.azure.com/.default` (from the Azure CLI login or the same identity as kubelogin). Configured per cluster.
3. **VPA recommender**, if installed: `VerticalPodAutoscaler.status.recommendation` (target, lower/upper bound). Kubism can create a VPA in `updateMode: Off` (recommendations only) for a workload, through `ClusterActions`.
4. **Local sampling** (fallback, no infrastructure needed): while the app runs, `MetricsService` samples metrics-server every 30 s for the clusters the user enabled it on and keeps the samples in a local store (SQLite in the app-data folder, 30 days, downsampled after 24 h). Gaps while the app is closed are shown as such; the report states how many hours of data it is based on.

**Model** (`RightSizing` in Core, pure and unit-tested): for each (cluster, namespace, workload, container), aggregated over all replicas and the chosen window (default 7 days, 1–30): CPU request/limit, CPU usage p50/p95/p99/max, throttled ratio; memory request/limit, working set p95/max, OOMKilled count; restarts; QoS class; replica count; data coverage (% of the window with samples). Workloads are resolved through owner references (Pod → ReplicaSet → Deployment, Pod → Job → CronJob, StatefulSet, DaemonSet); bare pods are their own workload.

**Verdicts** (per container, per resource): *no request* (BestEffort, unschedulable estimate), *over-provisioned* (p95 < 40 % of request, configurable), *under-provisioned* (p95 > request, or throttled > 10 %, or any OOMKill), *limit too tight* (max ≥ 90 % of a memory limit), *OK*, *not enough data* (coverage < 50 % or < 24 h). **Waste** = (request − p95) × replicas, summed per namespace and cluster, in cores and GiB, optionally in money with a price per vCPU-hour and GiB-hour set per cluster.

**Recommendation strategy** (`IRightSizingStrategy`, default mirrors KRR): CPU request = p95 usage (minimum 10m), CPU limit = none (or keep the current one, setting); memory request = max working set × 1.15 (minimum 32Mi), memory limit = memory request (or × 1.2, setting). Values are rounded to sensible steps (CPU 5m/10m/50m, memory Mi). The strategy, percentiles and margins are settings, and the report shows which strategy produced each number.

**Correcting.** For a selection of containers the dashboard produces the new `resources` blocks as:
- a **patch preview** (diff of `spec.template.spec.containers[*].resources`), applied through `ClusterActions.SetResources` (strategic merge patch; it **triggers a rollout**, said in the confirmation; production rules apply);
- **copyable snippets** for where the truth really lives: plain YAML, Helm values (`resources:` block per container), Kustomize patch. If the workload is managed by GitOps or Helm (`argocd.argoproj.io/instance`, `app.kubernetes.io/managed-by: Helm`, `kustomize.toolkit.fluxcd.io/*` labels or annotations), applying directly is disabled by default with "will be reverted by Argo CD/Flux/Helm: copy the snippet instead";
- a **CSV / Markdown export** of the report.

### Helm

Read-only from the cluster, without the helm binary: releases are Secrets of type `helm.sh/release.v1` (base64 → gzip → JSON). `HelmReleases` lists releases, revisions, status, chart, values and the rendered manifest. Write operations (rollback, uninstall) shell out to `helm` if found on PATH, through `ClusterActions`, otherwise they're disabled with a tooltip.

### Events

Like CinnabarSharp: state changes call `IClusterEventsService.Push(...)` with typed items (`CatalogChanged`, `SessionStateChanged`, `CacheChange`, `ActionCompleted`, `PortForwardChanged`…). Consumers subscribe to `IObservable<…>`; `DocumentEventsService`-style hand-rolled observable, no Rx dependency in Core (tests may use `System.Reactive.Linq`). Tests assert ordered event sequences.

### UI (`Kubism.Desktop`)

- Layout: **cluster catalog** on the far left (hotbar of icons/colors, like Lens), then for the active cluster a **sidebar** (Overview, Nodes, Workloads [Pods, Deployments, DaemonSets, StatefulSets, ReplicaSets, Jobs, CronJobs], Config [ConfigMaps, Secrets, ResourceQuotas, LimitRanges, HPA, PDB], Network [Services, Endpoints/EndpointSlices, Ingresses, IngressClasses, NetworkPolicies], Storage [PVC, PV, StorageClasses], Namespaces, Events, Helm, Access Control [ServiceAccounts, Roles, ClusterRoles, Bindings], Custom Resources [grouped by API group], plus a "All kinds" entry from discovery), a **resource list** (virtualized grid, namespace multi-select, text filter, sortable columns, multi-select for bulk delete), a **details panel** (slides in from the right: Overview, YAML, Events, Related/owners, Graph, Timeline), a **Compare** page (same kind in two clusters or namespaces), and a **bottom dock** with tabs for logs, terminals and port-forwards.
- Each connected cluster is a top-level **workspace tab**, so two clusters can be side by side; the window title and a colored strip show the active cluster and its safety mode (red strip for *production*).
- Grid: Avalonia `DataGrid` or `TreeDataGrid` with virtualization; rows are view models updated in place from `CacheChange` batches (`ObservableCollection` diffing, never a full reset except on `Resynced`).
- YAML editor: [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit) with `AvaloniaEdit.TextMate` (YAML grammar), read-only view and edit mode, server-side `managedFields` hidden by default, diff view on conflict.
- Terminal: an Avalonia terminal control built on an xterm-compatible engine. Candidates to evaluate in the Phase 7 spike: `XtermSharp.Avalonia`, `AvaloniaTerminal`/`SvcSystems.UI.Terminal` (XTerm.NET), `AgentSpace.Avalonia.XTerm`. Wrap the chosen one behind `ITerminalView` so it can be swapped; it must support resize events, UTF-8, 256 colors, copy/paste, and AOT.
- Long operations go through `MainViewModel.RunBusyAsync` (status-bar progress, cancel button).
- Menus as `MenuSpec` records turned into a macOS `NativeMenu` or an in-window `Menu` (reuse CinnabarSharp's approach, including the "mutate, never replace, the macOS native menu" rule). Shortcuts use `PlatformHotkeyConfiguration.CommandModifiers`. Command palette (⌘/Ctrl+K): jump to any kind, resource, namespace or cluster.
- Dialogs through `IDialogService` (confirmations with typed name, errors with the API message, file pickers); tests use `FakeDialogService`.

### MCP server

`Kubism --mcp` (headless, stdio) exposes read tools by default: `list_clusters`, `list_kinds`, `list_resources` (with label/field selectors), `get_resource` (YAML, secrets redacted), `get_events`, `get_logs` (bounded), `top_pods/nodes`, `rightsizing_report` (per workload/container, with recommendations and snippets), `describe` (object + events + owners). Mutating tools (`apply`, `delete`, `scale`, `rollout_restart`) are **off unless started with `--allow-writes`** and still go through `ClusterActions` (safety mode and production rules apply; production requires `--allow-production` on top). Same patterns as CinnabarSharp: tool bodies run through `McpContext.Run`, Core exceptions become `McpException`, never write to stdout outside the protocol.

## AOT, trimming and JSON

Releases are Native AOT like CinnabarSharp. Consequences:
- No reflection-based `System.Text.Json`: our own types go in `CoreJson`/`DesktopJson`/`McpJson` source-generated contexts (`AotJsonTests` fails otherwise). Kubernetes objects are `JsonNode`s, so they need nothing.
- YAML ⇄ JSON conversion with YamlDotNet's representation model (no `Deserializer<T>` on our types).
- `KubernetesClient` vs `KubernetesClient.Aot`: decided by the Phase 0 spike. Result so far (docs/adr/0001): `KubernetesClient` is enough since we only use it for config, the HTTP handler chain and WebSocket setup; the AOT compile passes on macOS arm64, the link and the three-OS run are still open, so releases are self-contained JIT builds for now. Keep trim/AOT warnings at zero in our own projects either way.

## Conventions

- Nullable reference types and implicit usings enabled; warnings as errors in Core and Kube.
- Every async method takes a `CancellationToken`; no `.Result`/`.Wait()`.
- Times shown as kubectl does (`5m`, `3h12m`, `4d`), computed from server timestamps with the local clock skew corrected (skew measured from the `Date` header at connect).
- User-visible strings go through `Translations.GetString` (English first, French second).
- Settings (`SettingsStore`, JSON in the per-OS app-data folder): window layout, catalog metadata (labels, colors, tags, safety modes), last namespace per cluster, column widths/order per kind, log buffer size, theme. Never store tokens or credentials: they stay in kubeconfig files or in the auth plugins' own caches.
- Logs (`AppLog`): `log.txt` in the app-data folder; never log request bodies, headers with tokens, or secret values.
- Tests: no test talks to a real cluster except `Kubism.Integration.Tests`. New object shapes for `FakeApiServer` go in `Kubism.Testing/Data/*.yaml`.

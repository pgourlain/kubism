# Kubism — Roadmap

Goal: a Lens-like Kubernetes desktop app, multi-cluster, running natively on **macOS, Linux and Windows**. Architecture, rules and conventions: [CLAUDE.md](CLAUDE.md).

## How to work through this file

- Phases are in order; finish a phase (tasks **and** its Validation) before starting the next. Inside a phase, tasks may be done in any order unless one names another.
- Each task is done when its code, its tests and any doc line it mentions are in. Tick `[x]` and add a short note (what was built, notable decisions) under the task, as in CinnabarSharp's tasks.md.
- **Automated** validation items must pass in CI on the 3 OSes. **Manual** items are checked by a human on real machines; the agent lists them as remaining, never ticks them.
- Every phase that adds UI adds at least one headless UI test that renders the new view against `FakeApiServer` and saves a screenshot.
- If a task turns out to be wrong or too big, split or rewrite it here first, then implement.

## Status of this iteration

Implemented by an agent on **macOS arm64 only**; nothing here has run on Linux, Windows or GitHub Actions yet.

* Verified locally: `dotnet build Kubism.slnx` (Core/Kube warnings-as-errors clean) and the five test projects, 286 tests pass (Core 164, Kube 18, Desktop headless 94, MCP 10; 4 integration tests skipped without `KUBISM_IT_KUBECONFIG`).
* Real clusters were **not** touched: everything runs against `FakeApiServer` / `FakePrometheus`, and `dotnet run --project Kubism.Desktop -- --demo` starts the app on them.
* Left open on purpose, listed under their tasks as **Open**: AOT spike result on 3 OSes, terminal-engine evaluation (ADR 0002), node shell and ephemeral debug container (optional), `IMetricsSource`, signed packages.
* Every **Validation** item stays unticked: *automated* ones need the CI matrix, *manual* ones need a human on real machines and real clusters (kind, AKS with Entra, 5 000 pods).

* Reported by the maintainer: the app was run on **Omarchy (Arch Linux)** and works. Which validation items that covered is not recorded, so none is ticked below.

Test environments for manual validation:
- [ ] macOS (Apple Silicon)
- [ ] Linux (Ubuntu 24.04 x64, X11 and Wayland/XWayland); Omarchy (Arch) run once by the maintainer, see above
- [ ] Windows 11 x64

Clusters for manual validation: a local **kind** cluster, an **AKS** cluster with Entra ID auth (`kubelogin`), and a cluster with **many objects** (≥ 5 000 pods, or a kind cluster loaded by `scripts/load-test.sh`).

---

## Phase 0 — Foundation

- [x] Repository layout: `Kubism.slnx`, `Directory.Build.props` (net10.0, nullable, implicit usings, warnings as errors for Core/Kube), `Directory.Packages.props` (central versions), `.editorconfig`, `.gitignore`, `LICENSE` (MIT), `README.md` (one paragraph + build commands).
  - Done. `global.json` selects the Microsoft.Testing.Platform runner (`dotnet test --project X`).
- [x] Projects from CLAUDE.md, empty but building: `Kubism.Core`, `Kubism.Kube`, `Kubism.Mcp`, `Kubism.Desktop` (Avalonia 12 MVVM template, CommunityToolkit.Mvvm, executable name `Kubism`), `Kubism.Testing`, and the test projects (xunit v3).
  - Done. Desktop tests pin `xunit.v3` 3.2.2 because `Avalonia.Headless.XUnit` is built against that extensibility API.
- [x] `CoreArchitectureTests`: Core references no Avalonia/Skia/System.Drawing/KubernetesClient assembly; `KubeArchitectureTests`: Kube references no Avalonia. Same mechanism as CinnabarSharp's.
  - Done (`CoreArchitectureTests`, `KubeArchitectureTests`, plus a source scan that no mutating `SendAsync` verb appears outside `ClusterActions`, and `AotJsonTests`).
- [x] `IKubeTransport` in Core (first version): `GetAsync(path)`, `ListAsync(gvr, ns, options)` returning `KubeList` (items as `JsonObject`, `resourceVersion`, `continue`), `WatchAsync(gvr, ns, resourceVersion, ct)` returning `IAsyncEnumerable<WatchEvent>`, `SendAsync(method, path, body, contentType)`, `StreamAsync(path)` for logs. `FakeKubeTransport` (in-memory) in `Kubism.Testing`.
  - Done. Extra members vs the first draft: `OpenSocketAsync` (exec/port-forward) and `ClockSkew`; list/watch carry Table cells.
- [x] `FakeApiServer` (Kestrel on `127.0.0.1:0`): `/version`, `/readyz`, discovery (aggregated v2 and legacy), list (JSON and `as=Table` with columns for pods/deployments/nodes/one CRD), watch (chunked NDJSON, bookmarks, injectable `410 Gone`, injectable disconnect), get/put/patch (merge, strategic merge for the kinds used, apply)/delete, `/log` streaming. Seed objects from `Kubism.Testing/Data/*.yaml`. Writes a temp kubeconfig pointing at itself (token auth).
  - Done, plus exec/port-forward WebSockets, `selfsubjectaccessreviews`, metrics, evictions with injectable PDB blocks, `FakePrometheus`, seed data in `Kubism.Testing/Data/demo.yaml`.
- [ ] **Spike: client library and AOT.** Small console in `spikes/aot-client/` that, published Native AOT on the 3 OSes, loads a kubeconfig with an exec plugin, lists pods as raw JSON through the configured HTTP client, watches pods for 30 s, and opens a WebSocket exec. Try `KubernetesClient` then `KubernetesClient.Aot`. Write the result in `docs/adr/0001-kubernetes-client.md` (which package, what we use from it, what we reimplement, AOT yes/no for 0.x releases) and update CLAUDE.md's "AOT" section.
  - **Open.** Partly done: `spikes/aot-client` + `run.sh` + ADR 0001. On macOS arm64 the AOT **compile** passes (warnings only in KubernetesClient/YamlDotNet) but the **link** failed locally (`-lssl`), so the runtime scenario is not verified; Linux/Windows not run. Releases stay JIT self-contained. See `spikes/aot-client/RESULT-osx-arm64.md`.
- [x] CI (`.github/workflows/ci.yml`): matrix windows/macos/ubuntu, `dotnet build` + `dotnet test`; job `integration` on ubuntu creating a kind cluster (`helm/kind-action`) and running `Kubism.Integration.Tests` with `KUBISM_IT_KUBECONFIG`; upload UI screenshots per OS.
  - Written (matrix + kind integration job + screenshot artifacts). **Not yet run on GitHub** (no runner here). Integration project exits with code 8 when every test is skipped: the CI job runs against kind so it executes them.
- [x] `docs/` folder with `architecture.md` pointing to CLAUDE.md, and `docs/adr/` for decisions.
  - Done: `docs/architecture.md`, `docs/adr/0001..0003`, `docs/mcp.md`.

**Validation**
- [ ] (automated) `dotnet test` green on the 3 OSes; integration job green with an empty smoke test (`GET /version`).
- [ ] (automated) Architecture tests fail when a forbidden reference is added (checked once by hand, then reverted).
- [ ] ADR 0001 written; spike results noted per OS.

## Phase 1 — App shell, kubeconfig catalog, connecting

- [x] `KubeconfigCatalog`: reads `KUBECONFIG` (merged, OS-specific separator), `~/.kube/config`, extra files and folders from settings; one `ClusterEntry` per context; duplicate context names across files are kept apart (id = file + context). Watches files (debounced 500 ms) and raises `CatalogChanged`. Unit tests with sample kubeconfigs (multi-context, relative cert paths, missing file, invalid YAML reported per file without breaking the others).
  - Done, tests cover multi-context, same context in two files, invalid YAML per file, extra folders, debounced file watching (500 ms), production regex default.
- [x] `ClusterSessionManager` + `ClusterSession` with the state machine from CLAUDE.md, `GET /version` on connect, `/readyz` health check every 30 s, backoff reconnect, `DisconnectAsync` cancels everything. Tests against `FakeApiServer` (connect, server down → `Degraded` → back up → `Connected`, disconnect cancels a pending call).
  - Done; tests: connect, server down → Degraded → Connected, disconnect cancels the session token and informers, two clusters at once.
- [x] Auth through Kube: certificates, token, token file, exec plugin. **PATH resolution for GUI apps** (`ShellEnvironment.ResolveLoginPath()`, timeout 3 s, cached) used for exec plugins. Errors mapped to clear messages: plugin not found (name + PATH searched), plugin failed (its stderr), 401, certificate untrusted, unreachable/timeout.
  - Done: certs/tokens via the library; exec plugins run by `ExecCredentialProvider` (cached until 60 s before expiry, refreshed on 401, stderr captured); login-shell PATH (`ShellEnvironment`, 3 s timeout, cached). Errors: plugin not found (name + PATH), plugin failed (stderr + exit code), 401, certificate untrusted, unreachable.
- [x] Interactive exec plugin output (device code): stderr lines shown live in a "Signing in to <cluster>" dialog with Cancel.
  - Done: stderr lines open the sign-in dialog (`IDialogService.ShowSignIn`) with Cancel; covered by a UI test with a fake `kubelogin` script.
- [x] Main window: menu bar (native on macOS), cluster catalog on the left (name, server host, status dot, color), welcome area with "Add kubeconfig file…", "Add folder…", and the list of contexts. Double-click or Connect opens the cluster in a workspace tab; Disconnect closes it.
  - Done (in-window menu; native `NativeMenu` built from the same `MenuSpec` on macOS).
- [x] Catalog metadata editor (per cluster): display name, color, icon letter, tags, safety mode (*normal*, *production*, *read-only*), default namespace. Stored in settings, never in kubeconfig. Contexts whose name matches `prod|prd|production` (setting, regex) start as *production*.
  - Done (code-built dialog). Stored in settings only.
- [x] `SettingsStore` (JSON, app-data folder), `AppLog`, `--demo` flag (starts `FakeApiServer` in-process and adds its kubeconfig).
  - Done, plus a Settings dialog (theme, language, log buffer, informer budget, right-sizing strategy, update opt-in).
- [x] Headless UI tests: catalog lists the fake contexts; connect shows the workspace tab; a failing context shows its error.
  - Done (`UiTests`, screenshots in `bin/<Config>/net10.0/screenshots/`).

**Validation**
- [ ] (automated) Catalog and session tests green on the 3 OSes.
- [ ] (manual) Connect to kind, and to AKS with `kubelogin` **when the app is launched from Finder/Explorer/desktop menu**, not from a terminal.
- [ ] (manual) Device-code login shows the code and URL; cancelling leaves the cluster disconnected without a hang.
- [ ] (manual) Editing `~/.kube/config` outside the app updates the catalog within a second.

## Phase 2 — Discovery and resource browser

- [x] `ApiDiscovery`: aggregated discovery with legacy fallback, `ResourceKind` list (preferred version per group), subresources noted (`scale`, `log`, `exec`, `portforward`, `eviction`, `status`). Tests: both formats, a group failing (e.g. a broken metrics APIService) doesn't break the rest and is reported once.
  - Done (aggregated + legacy; broken group reported once in `FailedGroups`; subresources).
- [x] `KubeObject` wrapper and `PodView`/`DeploymentView`/`NodeView`/`EventView` helpers; `PodView.Status` matches kubectl's STATUS column (CrashLoopBackOff, Init:0/1, Terminating, Completed, Evicted…). Table-driven tests from captured real objects.
  - Done. `PodView.Status` table-driven tests use synthetic objects shaped like kubectl's cases, not captured real ones.
- [x] `TableService`: Table-format list (`includeObject=Object`), column definitions per kind, cell formatting (dates → age, priority-0 columns shown by default, others in a "Columns…" chooser). Fallback columns when the server refuses `as=Table`.
  - Done; priority > 0 columns behind the "All columns" toggle; age cell recomputed locally.
- [x] Sidebar from CLAUDE.md (fixed sections for built-in kinds, "Custom Resources" grouped by API group from discovery, "All kinds" entry), kinds the user cannot `list` dimmed (`SelfSubjectAccessReview`, cached).
  - Done; forbidden kinds dimmed for built-in kinds (SelfSubjectAccessReview, cached).
- [x] `ResourceListView`: virtualized grid, sortable columns (numeric/age aware), namespace multi-select (remembered per cluster, "All namespaces"), text filter (name, labels with `key=value`), status coloring for pods/nodes, row count. **Static** in this phase (list once, Refresh button); live updates come in Phase 3.
  - Done and already live (Phase 3). Sorting/filtering are client-side; row colors by severity.
- [x] Namespaces from the cluster; when `list namespaces` is forbidden, let the user type namespaces (stored per cluster).
  - Done (manual namespaces stored per cluster).
- [x] Pagination: `limit=500` + `continue`, progress shown for long lists.
  - Done (`limit=500` + `continue`). No dedicated progress bar: the list shows "Loading…" until the first page set is applied.
- [x] Command palette (⌘/Ctrl+K): kinds (with short names: `po`, `deploy`, `svc`…), namespaces, clusters.
  - Done (⌘/Ctrl+K; short names rank first).
- [x] Headless UI tests: pods list with the fake's columns; a CRD appears under Custom Resources with its printer columns; filter and sort.
  - Done.

**Validation**
- [ ] (automated) Discovery, PodView status and Table tests green.
- [ ] (manual) On AKS and kind: every sidebar entry lists without error; a CRD (e.g. cert-manager `Certificate`) shows its own columns.
- [ ] (manual) 5 000 pods list scrolls smoothly; first page shows in < 2 s on AKS.

## Phase 3 — Live updates (informers)

- [x] Transport watch: chunked NDJSON parsed incrementally (`Utf8JsonReader` over a `PipeReader`), `allowWatchBookmarks=true`, `timeoutSeconds` 5–10 min with re-watch, typed `WatchEvent` (`Added/Modified/Deleted/Bookmark/Error`).
  - Done (`PipeReader` + `Utf8JsonReader`, bookmarks, `timeoutSeconds` 300-600 with re-watch, Table watch).
- [x] `ResourceCache` per (cluster, GVR, namespace): list → watch → apply; `410 Gone` → relist + `Resynced` diff; disconnect → re-watch from last bookmark with backoff; reference counting with 60 s linger; coalesced change batches (200 ms). Tests with `FakeApiServer` for each case, including "object deleted while disconnected appears as Deleted after resync".
  - Done with tests for list/watch/410/bookmark/disconnect/refcount/linger/coalescing and an end-to-end 410 against `FakeApiServer`.
- [x] `ResourceListView` binds to the cache: rows updated in place (no selection loss, no scroll jump), new rows briefly highlighted, deleted rows fade out; "Live" indicator, "Reconnecting…" when the informer is retrying.
  - Done: rows updated in place, new rows highlighted 3 s, deleted rows faded, Live/Reconnecting indicator.
- [x] Age column ticks (one timer for all visible rows).
  - Done (one workspace timer, visible list only, every 5 s).
- [x] Session `Degraded` when all informers fail; back to `Connected` when one succeeds.
  - Done.
- [x] Memory: caches for hidden views are released after linger; a debug panel (Help › Diagnostics) lists informers, object counts and last event time per cluster.
  - Done: linger release; Help › Diagnostics lists informers, object counts, last event; per-cluster informer budget (setting).

**Validation**
- [ ] (automated) Cache tests for list/watch/410/bookmark/disconnect green on the 3 OSes.
- [ ] (manual) `kubectl scale deploy x --replicas=10` then `0`: the pods list follows within a second, without flicker.
- [ ] (manual) Laptop sleep 10 min then wake: lists recover without restarting the app; no duplicated rows.
- [ ] (manual) Two clusters connected, each with 3 lists open, for 1 hour: memory stays stable.

## Phase 4 — Details, YAML and editing

- [x] Details panel: Overview per kind (metadata, labels/annotations as chips, conditions table, kind-specific sections: pod containers with image/state/restarts/probes/ports/env (secret refs shown as references), deployment replicas and strategy, service ports and endpoints, node capacity/allocatable/taints), Events tab (field selector `involvedObject.uid`), Related tab (owner chain up and owned objects down via `ownerReferences`, services selecting a pod).
  - Done (Overview per kind, Events via `involvedObject.uid`, Related via owner references and service selectors).
- [x] YAML tab with AvaloniaEdit + TextMate YAML grammar, `managedFields` hidden by default (toggle), copy, download.
  - Done (TextMate YAML grammar, managedFields toggle, copy, download).
- [x] Edit mode: edit YAML, Save = `PUT` with the read `resourceVersion` through `ClusterActions.ApplyYaml`; `409 Conflict` → diff view (yours vs server) with "Reload and reapply my changes" / "Discard mine". Validation errors from the API shown inline at the top with the API message.
  - Done: PUT with the read resourceVersion, 409 → diff + "Reload and reapply" / "Discard mine", API errors inline.
- [x] "Create from YAML…" (multi-document, server-side apply with `fieldManager=kubism`, per-document result list).
  - Done (multi-document, server-side apply, per-document results).
- [x] Secrets: values masked (`••••`), per-key Reveal (and copy decoded value), never in logs/screenshots/tests. ConfigMaps are shown in clear.
  - Done (details, YAML tab, MCP; snapshots are taken before any reveal).
- [x] Headless UI tests: details of a pod, edit conflict flow against `FakeApiServer`, secret masked in the rendered screenshot.
  - Done.

**Validation**
- [ ] (automated) Edit/conflict/apply tests green.
- [ ] (manual) Edit a deployment's image on kind: rollout happens; editing the same object in kubectl meanwhile triggers the conflict flow.
- [ ] (manual) A Secret never shows a value until revealed, including in the YAML tab.

## Phase 5 — Actions and safety

- [x] `ClusterActions` with `SafetyPolicy` and `ActivityLog` as specified in CLAUDE.md; `ClusterActionsTests.Actions` lists every action and checks: refused in read-only, requires confirmation token in production, logged, events raised.
  - Done; `[ClusterAction]` attribute + test that every public action is listed.
- [x] Actions: Delete (single and bulk, propagation policy, grace period, force for stuck Terminating pods with a warning), Scale (dialog with current/desired), Rollout restart, Pause/Resume rollout, Cordon/Uncordon, Drain (options like kubectl: ignore DaemonSets, delete emptyDir data, timeout; eviction API, PDB-blocked evictions shown and retried; cancellable), Trigger CronJob, Suspend/Resume CronJob.
  - Done (UI uses default drain options; the API supports all of them).
- [x] Confirmation dialogs: normal clusters → Yes/No; *production* → type the resource name (or the count for bulk); read-only → buttons hidden or disabled with tooltip.
  - Done.
- [x] RBAC: buttons disabled with the `SelfSubjectAccessReview` reason.
  - Done.
- [x] Activity panel (per cluster): what this app did, when, result; exportable.
  - Done (CSV export).
- [x] Context menu on rows and details header with the same actions; keyboard shortcuts (Delete key with confirmation, ⌘/Ctrl+E edit, L logs, S shell).
  - Done (Delete, Ctrl+E, L, S).

**Validation**
- [ ] (automated) Safety tests green; no mutation reachable without `ClusterActions` (architecture test: no `SendAsync` with a mutating verb outside `ClusterActions`).
- [ ] (manual) On a cluster marked *production*, deleting a pod requires typing its name; read-only refuses even through the MCP server (Phase 12).
- [ ] (manual) Drain a kind worker node with a PDB in place: progress shows the blocked pod and completes once it's allowed.

## Phase 6 — Logs

- [x] `LogSession`: follow, timestamps, container picker (init and ephemeral containers included), previous (crashed) container, tail lines / since, ring buffer (setting), reconnection when the stream drops while the pod still runs.
  - Done; "previous" is offered on pods that restarted.
- [x] Workload logs: all pods of a deployment/statefulset/daemonset/job, merged by timestamp, colored pod prefix, new pods joining automatically (from the cache).
  - Done.
- [x] Logs view in the bottom dock (one tab per session): virtualized lines, search with highlight and next/previous, regex toggle, wrap, pause/resume, clear, download (whole buffer or "from start" re-fetch), ANSI colors rendered.
  - Done on AvaloniaEdit (search + regex, wrap, pause, clear, download of the buffer, ANSI foreground colors). "Download from start" re-fetch is not implemented.
- [x] JSON log lines: optional pretty/collapsed view with level coloring.
  - Done (pretty one-liner with level).

**Validation**
- [ ] (automated) Log streaming, ring buffer bound and merge-order tests.
- [ ] (manual) A pod logging 1 000 lines/s for 5 min: UI stays responsive, memory bounded.
- [ ] (manual) Crashlooping pod: "previous" shows the crash output.

## Phase 7 — Shell (exec)

- [ ] **Spike: terminal control.** Evaluate `XtermSharp.Avalonia`, `AvaloniaTerminal`/`SvcSystems.UI.Terminal` and `AgentSpace.Avalonia.XTerm` on: Avalonia 12, AOT, resize events, UTF-8/emoji, 256/true color, copy/paste, mouse mode, license, maintenance. Record the choice in `docs/adr/0002-terminal.md`; wrap it behind `ITerminalView`.
  - **Open.** Not completed as specified: ADR 0002 records why a small in-house engine (`TerminalBuffer` + `TerminalControl` behind `ITerminalView`) was used and what is still to evaluate.
- [x] `ExecSession`: WebSocket exec (v5, fallback v4), TTY, stdin, resize channel wired to the terminal, shell detection (`bash` → `sh`, or user command), exit status shown, reconnect button.
  - Done (v5 → v4 fallback, TTY, resize, status/exit, reconnect button, login-shell trick for bash→sh).
- [x] Terminal tabs in the bottom dock (pod/container in the title), "Open shell" from pod rows and details, container picker.
  - Done.
- [ ] Node shell (optional, behind a setting): creates a privileged debug pod on the node through `ClusterActions` and removes it on close; clearly labelled.
  - **Open.** Not implemented (optional).
- [ ] `kubectl debug`-like ephemeral container (optional): add an ephemeral container with an image chosen by the user, then exec into it.
  - **Open.** Not implemented (optional).

**Validation**
- [ ] (automated) Exec channel framing and resize tests against `FakeApiServer` (fake exec endpoint echoing stdin).
- [ ] (manual) `vim`, `top` and `htop` work in a shell; window resize reflows; copy/paste works with the OS shortcuts on each OS.
- [ ] (manual) Shell into a distroless container fails with a clear message.

## Phase 8 — Port-forward

- [x] `PortForwardManager`: local listener on 127.0.0.1 (chosen or free port), one stream per connection, pod or service target (service → ready pod + targetPort, re-resolved if the pod goes away), status per forward (active connections, errors).
  - Done.
- [x] UI: "Forward port…" from pod/service ports, list of forwards per cluster (stop, copy URL, open in browser), indicator in the cluster tab.
  - Done.
- [x] Forwards stop on disconnect; "keep" forwards restart when the cluster reconnects.
  - Done; "Keep" forwards are stored per cluster and restarted on the next connect.

**Validation**
- [ ] (automated) Port-forward framing tests (fake endpoint) and service→pod resolution tests.
- [ ] (manual) Forward a web service on kind and AKS, load the page in a browser, kill the pod: the forward resolves to the new pod.

## Phase 9 — Metrics and cluster overview

- [x] `MetricsService` (`metrics.k8s.io`), ref-counted polling, 15 min history, "metrics not available" state.
  - Done.
- [x] CPU/memory columns on pods and nodes lists (usage, and % of requests/limits as a small bar); sparklines in details.
  - Done as text with % of requests/allocatable (no mini bar); sparklines in the details panel for pods and nodes.
- [x] Cluster Overview page: nodes ready/total, pods by phase, CPU/memory usage vs capacity vs requests, recent Warning events, problem pods (CrashLoopBackOff, Pending > 5 min, ImagePullBackOff), Kubernetes version.
  - Done.
- [ ] `IMetricsSource` abstraction; Prometheus source (optional, URL per cluster, or auto-detect a Prometheus service and reach it through the API server proxy).
  - **Open.** Not done: Prometheus is used for *history* (`IUsageHistorySource`) only; live metrics come from metrics-server.

**Validation**
- [ ] (automated) Metrics parsing (quantities `250m`, `1Gi`…) and aggregation tests.
- [ ] (manual) Overview on AKS matches `kubectl top nodes` and `kubectl get pods -A` within reason.

## Phase 9b — Right-sizing (requests/limits vs real usage)

See "Right-sizing" in CLAUDE.md. Depends on Phase 9.

- [x] `IUsageHistorySource` + `UsageQuery` (cluster, namespace filter, window, step) returning per-container series and percentiles. Implementations: `PrometheusUsageSource` (URL or API server service proxy; PromQL with `quantile_over_time` / `max_over_time`, queries batched per namespace to stay under server limits), `AzureManagedPrometheusSource` (query endpoint + Entra token; ADR `0003-azure-monitor-auth.md` decides between Azure CLI credential and reusing kubelogin's login), `VpaRecommendationSource`, `LocalSamplerSource`. Source chosen per cluster in the catalog metadata, with "Test connection".
  - Done: Prometheus (URL or API-server proxy, batched per namespace), Azure managed Prometheus (`ITokenProvider` → Azure CLI, ADR 0003), VPA, local sampling. "Test connection" button in the dashboard.
- [x] `LocalSampler`: background sampling of metrics-server (30 s) for clusters where it is enabled, SQLite store in app-data, downsampling after 24 h, 30-day retention, size shown in Diagnostics; sampling pauses when the cluster is disconnected and the gap is recorded.
  - Done with a file-based store (JSONL per day, 5-minute downsampling after 24 h, 30-day retention, size in Diagnostics) instead of SQLite, to keep Core free of native dependencies.
- [x] `RightSizing` model in Core: workload resolution through owner references, aggregation across replicas, percentiles, throttling, OOMKills (pod `lastState.terminated.reason` when kube-state-metrics is absent), QoS, coverage; verdicts and waste as in CLAUDE.md. Table-driven unit tests with synthetic series (steady, spiky, growing memory, OOM, idle, too little data).
  - Done with table-driven tests.
- [x] `IRightSizingStrategy` with the default (KRR-like) strategy, settings for percentiles, margins, minimums, CPU limit policy and memory limit policy; rounding rules tested.
  - Done.
- [x] **Right-sizing dashboard** (sidebar entry under Overview): KPIs (requested vs used CPU/memory, waste in cores/GiB and optional money, # under-provisioned, # without requests), grid of workloads → expandable containers with, per resource, a bar showing p95 and max usage against request and limit, verdict chip, recommended values; filters (namespace, verdict, workload kind, min waste), sort by waste or risk; window selector (24 h / 7 d / 14 d / 30 d); data source and coverage shown at the top.
  - Done as a flat grid (workload/container rows) rather than expandable workload rows.
- [x] Container detail: usage time series over the window with request and limit drawn as lines, OOMKill and restart markers, throttling series when available.
  - Partly: CPU and memory series with request/limit lines; no OOMKill/restart markers or throttling series.
- [x] Correcting: multi-select containers → patch preview (diff), "Apply" through `ClusterActions.SetResources` (rollout warning, production confirmation, disabled for GitOps/Helm-managed workloads unless overridden), "Copy as YAML / Helm values / Kustomize patch", "Create VPA (recommendation only)" through `ClusterActions`.
  - Done (preview, apply with rollout warning + production confirmation, GitOps/Helm block with override, YAML/Helm/Kustomize snippets, VPA).
- [x] Export: CSV and Markdown report (per cluster, filters applied, strategy and window stated).
  - Done.
- [x] Namespace and cluster roll-ups (requested vs used vs node allocatable), so over-requesting that blocks scheduling is visible.
  - Done.
- [x] `FakeApiServer` + a fake Prometheus endpoint (`Kubism.Testing/FakePrometheus`) with canned series, so the dashboard has headless UI tests and screenshots.
  - Done.

**Validation**
- [ ] (automated) Percentiles, verdicts, waste, rounding and owner resolution tests green; dashboard screenshot from the fake data.
- [ ] (manual) On a cluster with Prometheus (kind + kube-prometheus-stack): the recommendations match Robusta KRR's on the same window within one rounding step.
- [ ] (manual) On AKS with Azure Monitor managed Prometheus: the dashboard loads a 7-day window for a namespace of ~200 pods in < 10 s.
- [ ] (manual) Apply a recommendation on kind: rollout happens, the dashboard shows the new requests; an Argo CD-managed workload refuses direct apply and offers the snippet.

## Phase 10 — Multi-cluster experience

- [x] Several clusters connected at once, each in its own workspace tab; tabs show color, name, safety strip; switching keeps each cluster's sidebar selection, filters and dock tabs.
  - Done (per-workspace sidebar selection, namespaces, dock).
- [x] Split view: two clusters side by side (same kind, e.g. compare deployments between staging and production).
  - Done.
- [x] Global search across connected clusters (name contains, label selector) with results grouped by cluster.
  - Done (⌘/Ctrl+Shift+F).
- [x] Hotbar: pin clusters, reorder by drag, keyboard ⌘/Ctrl+1…9.
  - Done: pin, move up/down (no drag and drop), ⌘/Ctrl+1…9.
- [x] Per-cluster resource budget: max informers and watch connections (setting), so many connected clusters stay cheap.
  - Done (informers; the budget message is shown in the list).

**Validation**
- [ ] (manual) 5 clusters connected (kind ×3 + AKS ×2): switching is instant, CPU idle when nothing changes, memory reported in Diagnostics.

## Phase 11 — Helm and custom resources

- [x] `HelmReleases`: decode `helm.sh/release.v1` secrets; releases list (namespace, chart, app version, revision, status, updated), details (values, user-supplied vs computed, manifest, notes, history).
  - Done (list, values user/computed, manifest, notes, history).
- [x] Rollback / uninstall through `helm` CLI if found (through `ClusterActions`), disabled with a tooltip otherwise.
  - Done through `helm` + `ClusterActions`, disabled with a tooltip without helm.
- [x] CRD browser: CRD list, schema view (OpenAPI v3 from the CRD, collapsible), instances list per version.
  - Done (CRD list, schema table from the storage version, versions, "Browse instances").
- [x] Generic "related" for CRs via owner references.
  - Done.

**Validation**
- [ ] (automated) Helm release decoding test with a captured secret.
- [ ] (manual) On a cluster with ingress-nginx and cert-manager installed by Helm: releases, values and history match `helm list/history/get values`.

## Phase 12 — MCP server

- [x] `Kubism.Mcp` with the read tools from CLAUDE.md, resources `kubism://clusters`, `kubism://clusters/{id}/kinds`; secrets redacted at the tool layer and tested.
  - Done.
- [x] Write tools behind `--allow-writes`, production behind `--allow-production`, all through `ClusterActions`.
  - Done.
- [x] `docs/mcp.md` (setup for Claude Code / Claude Desktop, tools, safety).
  - Done.
- [x] `Kubism.Mcp.Tests` driving `Kubism --mcp` against `FakeApiServer`.
  - Done (spawns the real executable).

**Validation**
- [ ] (automated) MCP tests green; a test proves no secret value can leave through any tool.
- [ ] (manual) Claude Code diagnoses a crashlooping pod on kind through the MCP server (events + logs + describe).

## Phase 13 — Polish and packaging

- [x] Theme: light/dark following the OS, cluster colors readable in both; icons for kinds and actions (vector, one set).
  - Done (follows the OS by default; setting). Icons: a small vector set for the sidebar only.
- [x] Localization: English, French.
  - Done for menus, sidebar, main actions (English, French); other strings are English.
- [x] Accessibility: keyboard navigation everywhere (sidebar, grid, details, dock), contrast check test like CinnabarSharp's `AccessibilityTests`.
  - Partly: automation names on key controls, WCAG AA contrast test for both themes, keyboard-reachable controls checked; no full keyboard walkthrough of every view.
- [x] About dialog, Help › Open Log Folder, "Copy diagnostics" (versions, OS, connected clusters' server versions, no names/tokens).
  - Done.
- [x] Update check on GitHub releases (opt-in, at most once a day, never installs).
  - Done (opt-in, ≤ once a day, never installs; tested with a stub handler).
- [x] `packaging/package.sh` and release workflow: Windows zip, macOS `.app`/`.dmg`, Linux tarball + `.desktop`, Arch package; Native AOT if ADR 0001 allows it.
  - Script, `.desktop`, PKGBUILD and release workflow are in; the macOS `.app`/`.dmg` was built and started locally. Windows/Linux packaging and Native AOT are untested here.
- [ ] Signed packages (later): Windows code signing, macOS Developer ID + notarization, Linux AppImage/Flatpak.
  - **Open.** Not done (marked later).

**Validation**
- [ ] (manual) Fresh machine of each OS: download, start, add a kubeconfig, connect, list pods, open a shell.

## Phase 14 — Understand and compare (first batch of the "what to add" list)

- [x] **Relation graph** (`Related`'s big sibling): `RelationGraph` in Core builds the neighbourhood of any object from objects already loaded (owners and owned, Service → Pod by selector, Ingress → Service, workload → ConfigMap/Secret/PVC/ServiceAccount from the pod template, PVC → PV, Pod → Node, HPA → target) with a top-to-bottom layered layout and per-node health. Shared config is a leaf (never expanded) unless it is the root; scaled-down ReplicaSets are skipped; bounded to 40 nodes / 5 hops, with a "more not shown" flag. Desktop: `RelationGraphControl` (custom-drawn, click to open, tooltip) in a new **Graph** tab of the details panel, loaded only when the tab is shown, panel widens to fit. Secret `data` is dropped from the list response before anything is built.
  - Done. Tests: `ExplorationTests` (Core), `UiExplorationTests` (headless, screenshot `30-relation-graph`).
- [x] **Helm in the graph**: a `HelmRelease` node (synthetic, from `HelmReleaseInfo`: latest revision and status read from the release secrets' labels, no decoding) sits above the objects carrying `meta.helm.sh/release-name` (or the managed-by/instance labels) that no controller in the graph owns; shown but not expanded when reached from an object, expanded when it is the root. Graph tab on the Helm page for the selected release; clicking a release box opens the Helm page on it. Shared `GraphLoader`; release payloads are never downloaded for the graph's generic secrets (`type!=helm.sh/release.v1`), only the labelled ones, with `data` dropped. `--demo` gains an `api` release.
  - Done. `HelmGraphTests` (Core), two UI tests, screenshots `33-graph-helm`, `34-helm-release-graph`.
- [x] **Compare clusters**: `ClusterCompare` + `LineDiff` in Core (server noise stripped: uid, resourceVersion, managedFields, status, clusterIP…; Secret values never in the diff, differing keys marked `•••• A` / `•••• B`), `CompareViewModel`/`CompareView` (sidebar entry *Compare*, plus **Compare…** on every object), two namespaces may differ (matched by name). `--demo` now adds a third, drifted cluster (`demo-dev`).
  - Done. Same tests; screenshot `31-compare-clusters`.
- [x] **Global search v2**: `SearchQuery` (`text`, `key=value`, `image:`, `ns:`, `kind:`, combined with AND), hits show the matching image.
  - Done. Screenshot `32-search-by-image`.
- [x] README with features and screenshots (`docs/screenshots/`, rendered by `DocsScreenshotTests`), install instructions in the release notes (`packaging/release-notes.md`, used by `release.yml` as CinnabarSharp does).
  - Done.
- [x] Event timeline per workload: events, rollouts, restarts and OOM kills on one time axis.
  - Done. `EventTimeline` in Core (events of the workload, its replica sets and pods; one rollout per replica set revision with its image; pod creations; last termination of each container, OOM kills flagged; failing conditions), Timeline tab in the details panel (workloads, CronJob, Pod), "Problems only" filter. `--demo` gets a rollout story for `web` and an OOM kill for `crashy-0`. `EventTimelineTests`, `UiExplorationTests`, screenshots `35-timeline`, `36-timeline-problems`. Limit: a pod only reports its *last* termination, and events expire after about an hour on a real cluster.
- [ ] Ephemeral debug: `kubectl debug` natively (ephemeral container, node shell, pod copy) through `ClusterActions` (see Phase 7's open items).
- [x] Security page (read-only): `WorkloadAudit` (pod spec rules), `RbacAnalysis` (risky roles and bindings; rules a user, group or service account holds, through its groups) and `CertificateScan` (TLS secrets and cert-manager Certificates), in Core with `SecurityTests`; `SecurityViewModel`/`SecurityView` with three lazily loaded tabs. Secret handling: only `tls.crt` is parsed, never the key, nothing kept. `--demo` gets a sloppy `worker`, a hardened `checkout`, risky RBAC and four certificates; `FakeApiServer` serves rolebindings and clusterrolebindings. Screenshots `37`–`39`.
  - Done. **Open**: Trivy Operator reports (CRDs) per workload, kubeconfig client certificates, `can-i` for arbitrary subjects through the API (the analysis reads RBAC objects, so it does not see aggregated ClusterRoles, webhook authorizers or cloud IAM), exposing the audits through the MCP server.
- [ ] Ecosystem views over CRDs: Argo CD (sync, health, diff; sync button through `ClusterActions`), Flux, cert-manager, Gateway API / Istio, KEDA, Karpenter, Crossplane / Azure Service Operator resource trees.
- [ ] Capacity and cost: node requests vs allocatable and fragmentation, unschedulable pods with the reason, cost per namespace or label with a trend, "what if I apply the recommendations" simulator.
- [ ] Workflow: saved views (filters, columns, namespaces per cluster), favourites and history in the command palette, playbooks (drain + upgrade + uncordon, with dry-run), OS notifications (CrashLoop, NotReady, stuck rollout), diagnostic bundle (describe + events + logs, secrets masked) as a zip.
- [ ] AI: "explain this error" panel (pod + events + logs, secrets masked) on top of the existing MCP read tools.
- [ ] AKS: node pool and upgrade info, Azure Monitor / Container Insights links, Workload Identity check (ServiceAccount annotation vs federated credential).

**Validation**
- [ ] (automated) `ExplorationTests` and `UiExplorationTests` green on the 3 OSes.
- [ ] (manual) On kind and AKS: the graph of a deployment shows its ingress, service, pods and config; opening a node from the graph works; comparing two real clusters (a namespace in staging and production) lists the expected drift and no Secret value.

## Criteria for 1.0

- [ ] Validation checklists of every phase passed on real macOS, Linux and Windows machines.
- [ ] Used daily for two weeks on real clusters (including AKS with Entra ID) without falling back to Lens/k9s for the covered features.
- [ ] No mutation path outside `ClusterActions`; production and read-only modes verified through the UI and MCP.
- [ ] 5 000-pod cluster: lists, filters and live updates stay smooth; memory bounded with 5 clusters connected.
- [ ] Signed packages on macOS and Windows.
  - **Open.** Not done (marked later).

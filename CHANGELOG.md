# Changelog

All notable changes to Kubism are listed here. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow [Semantic Versioning](https://semver.org/). Entries before 0.3.8 were reconstructed from the git history.

## [Unreleased]

### Added
- Workloads › *Images*: the images the pods of the selected namespaces run, one row per reference with registry, repository, version, the digest the node resolved, pods, workloads, namespaces and pull policy. Flags `latest`/untagged images, repositories running in several versions and images that fail to pull; filters, *Repositories* and *Registries* tabs, and the workloads that use the selected image (double-click opens one).
- Images: *Compare* tab lines up what each workload container runs in this cluster and another connected one (same namespaces, matched by name) and says whether only the version differs; *Export CSV* / *Export Markdown*.
- MCP: read tools `list_images` and `compare_images`.

## [0.3.9] - 2026-11-03

### Added
- Typed-name confirmation dialog: the name is selectable and a *Copy name* button copies it, so long names can be pasted instead of typed (#11).
- File › *Connect an AI Agent…* shows the exact `claude mcp add` command and the `mcpServers` JSON (Claude Desktop, VS Code, Cursor) for the installed executable, so the MCP server can be set up without the command line.

## [0.3.8] - 2026-10-26

### Added
- Right-sizing: compact action panel with labelled groups and tooltips; recommendation tooltips.
- Right-sizing: the detail loads asynchronously with a loading bar, Escape closes it, columns are resizable.
- Helm grids: resizable columns (#7).
- Recent warnings are selectable; selection color matches the YAML editor (#9).

### Fixed
- YAML output quotes capitalised booleans so a condition `status` stays a string.
- Exec falls back to `v4.channel.k8s.io` when the WebSocket upgrade is refused with 403.

## [0.3.7] - 2026-10-23

### Added
- Pod CPU/memory usage shown as % of request and of limit.
- Terminal right-click menu, Ctrl/Shift+Insert copy/paste.
- Right-click menu (Copy, Select all) in the logs and YAML editors.
- Helm values and manifest are highlighted like the YAML editor.
- Translucent square selection in text editors.

### Changed
- Prometheus: uses the service's real port and accepts `namespace/service:port`.
- The right-sizing error is shown on its own wrapping line.

### Fixed
- The YAML editor is read-only from construction; the edit buffer is dropped when another object is selected.
- Console windows of exec auth plugins and helper processes are hidden (Windows).

## [0.3.6] - 2026-10-14

### Fixed
- `Main` stays synchronous so the UI thread is STA on Windows (clipboard and drag and drop failed with `CO_E_NOTINITIALIZED`).

## [0.3.5] - 2026-10-13

### Changed
- Full stack traces are logged for caught exceptions.

## [0.3.4] - 2026-10-12

### Added
- Namespace picker in the navigation bar.
- Badges for unrouted ports in the network graph.

### Changed
- Clipboard errors and crashes are logged.

## [0.3.3] - 2026-10-11

### Added
- Navigation history per cluster (back/forward, ⌘[ / ⌘], Alt+←/→, mouse buttons) that restores the page as it was.
- Usage pages: resizable columns with a tooltip on cut text.

### Changed
- Navigation bar uses flat icon buttons.

## [0.3.2] - 2026-10-11

### Added
- Usage pages (Config, Storage, Network, Access Control): which ConfigMaps, Secrets, PVCs, PVs, StorageClasses, ServiceAccounts and IngressClasses are referenced, by whom, and which references are broken.
- Unread ConfigMap keys, missing Ingress backends, HPAs and PDBs attached to nothing.
- Catalog: remove or hide clusters, fixed rail, clearer connected state.

### Changed
- The window fits the screen.
- macOS packages keep only the target architecture in every Mach-O (checked in CI and in the release).
- Views use `PlaceholderText` instead of the obsolete `Watermark`.

### Fixed
- Flaky `ResourceCache` test (waits for the health change).

## [0.3.1] - 2026-10-10

### Fixed
- Static tokens are now sent with every request (the client library never applied them to the raw HTTP client).
- The client certificate is always offered; on Windows it was filtered out, giving 401 on every request with a kubeconfig client certificate. WebSockets use the same client. Covered by mutual TLS tests.
- Informer: the "no subscriber" check and the stop are one step (a late `Subscribe` got a stopped informer).

## [0.3.0] - 2026-10-10

### Added
- **RBAC graph** page (chain, subject, resource) with exact authorizer semantics, risk flags, namespace filter, API server verification, and MCP tools `rbac_who_can` / `rbac_rights_of`. See ADR 0004.
- **Network graph** page (exposure, ingress, service, workload, ports, problems), Routes tab (URL → Ingress → Service → pods), optional nodes column, namespace filter, MCP tool `network_exposure`. See ADR 0005.
- Right-sizing details follow the clicked container.
- README sections with screenshots for both graphs.

### Changed
- *Check for updates* from the menu ignores the opt-in and daily limit and reports the result or failure; versions compare on three parts.
- Client certificate details, kubeconfig user keys, token kind/JWT expiry and 401/403 details are logged to diagnose Unauthorized.

### Fixed
- Logs: following scrolls to the last line instead of overshooting into blank space.
- RBAC chain: node details scroll and wrap.

## [0.2.1] - 2026-10-10

### Fixed
- Exec auth: one plugin run for parallel 401s (intermittent Unauthorized on Windows).

### Changed
- The log file records transport, exec plugin, informer and session failures.

## [0.2.0] - 2026-10-10

### Added
- **Helm**: edit values like Lens with a change preview and upgrade; Uninstall and Roll back in a bar above the tabs; list refresh after changes.
- **Capacity and cost** page: node room, unschedulable pods, cost by namespace or label, right-sizing what-if.
- **GitOps** page: Argo CD and Flux with sync, refresh, reconcile and suspend through `ClusterActions`.
- **Security** page: workload audit, RBAC findings, who-can-do-what, certificate expiry.
- **Timeline** tab: events, rollouts, pod creations, OOM kills and conditions on one time axis.
- Relation graph (with Helm releases), compare clusters, global search by image.
- README with features and screenshots; release notes carry install instructions.

### Fixed
- Logs: a flush queued before pausing no longer shows lines received while paused.

## [0.1.0] - 2026-10-10

First release.

### Added
- Multi-cluster catalog from kubeconfig files with labels, colors, tags and safety modes; one workspace tab per connected cluster; split view.
- Live resource lists built on informers (list → watch → relist), server-side Table columns, details panel with Overview, YAML and Events, bulk delete.
- `ClusterActions` as the only mutation path (safety policy, activity log), with RBAC-aware buttons.
- Logs, terminal (exec), port-forward (with "keep"), metrics.
- Right-sizing with Prometheus, VPA and local sampling; patch preview and snippets.
- Helm releases read without the `helm` binary, with a one-glance diagnosis for failed releases.
- Secret values masked everywhere.
- MCP server (`Kubism --mcp`) with read tools and opt-in write tools, global search, settings, update check, CRD schema.
- Native AOT releases, own kubeconfig reader, third-party notices, About dialog, Arch/Omarchy package, macOS dmg with Applications shortcut.
- CI and release workflows, headless UI tests, `FakeApiServer`, integration tests against kind.

[Unreleased]: https://github.com/pgourlain/kubism/compare/v0.3.8...HEAD
[0.3.8]: https://github.com/pgourlain/kubism/compare/v0.3.7...v0.3.8
[0.3.7]: https://github.com/pgourlain/kubism/compare/v0.3.6...v0.3.7
[0.3.6]: https://github.com/pgourlain/kubism/compare/v0.3.5...v0.3.6
[0.3.5]: https://github.com/pgourlain/kubism/compare/v0.3.4...v0.3.5
[0.3.4]: https://github.com/pgourlain/kubism/compare/v0.3.3...v0.3.4
[0.3.3]: https://github.com/pgourlain/kubism/compare/v0.3.2...v0.3.3
[0.3.2]: https://github.com/pgourlain/kubism/compare/v0.3.1...v0.3.2
[0.3.1]: https://github.com/pgourlain/kubism/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/pgourlain/kubism/compare/v0.2.1...v0.3.0
[0.2.1]: https://github.com/pgourlain/kubism/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/pgourlain/kubism/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/pgourlain/kubism/releases/tag/v0.1.0

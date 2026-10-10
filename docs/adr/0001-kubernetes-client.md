# ADR 0001: Kubernetes client library and Native AOT

Status: accepted for 0.x; AOT results pending per OS. Date: 2026-10.

## Decision
Use the official **`KubernetesClient`** (v19) for exactly three things:

1. kubeconfig parsing and credentials: certificates, tokens, CA bundles, impersonation (`KubernetesClientConfiguration`);
2. the configured `HttpClient` handler chain (TLS, proxies, cert validation) that `KubeTransport` uses for **raw JSON** list/get/patch/delete and the chunked watch stream;
3. WebSocket connection setup for exec, attach and port-forward (`StreamConnectAsync`).

Everything else is ours: discovery, Table lists, informers, watch parsing (`Utf8JsonReader` over a `PipeReader`), log streaming, the channel protocol framing, port-forward multiplexing, and **exec credential plugins** (`ExecCredentialProvider`, so stderr device-code prompts reach the sign-in dialog and tokens refresh before `expirationTimestamp` and on a 401; the library's own exec support is removed from the loaded config).

We do **not** use the typed models or `Watch<T>`.

## Findings
* The library never attaches bearer tokens to plain `http://` servers; `KubeTransport` adds them itself in that case, which only the fake API server uses.
* After removing the `exec` section the library refuses a user without credentials; a placeholder token is set and our handler overrides the header.
* `KubernetesClient.Aot` was not needed: nothing we call depends on reflection-based model serialization.

## AOT status (updated)
**Releases are Native AOT.** What it took:

* `KubernetesClient`'s kubeconfig loader deserializes into types by reflection and fails at runtime under AOT. Kubism reads the kubeconfig itself (`KubeConfigReader`, YamlDotNet representation model) and builds the `KubernetesClientConfiguration` from it; exec plugins were already ours. With that, list, watch, patch and the exec/port-forward WebSockets run in the AOT binary (spike `spikes/aot-client`: version, Table list, watch and a v5 exec socket against the fake API server).
* The remaining trim/AOT warnings come from `KubernetesClient` (model JSON converters we never use), `YamlDotNet` and Avalonia's DataGrid internals; none is in our code paths.
* Verified on macOS arm64 with the official SDK (the Homebrew SDK cannot link: `-lssl`): the AOT executable was driven by hand (catalog, connect, live lists, details with the TextMate YAML editor, logs, an exec terminal, right-sizing against the fake Prometheus, command palette, Cmd+Q in 0.15 s) and the MCP test-suite passes against it (`KUBISM_MCP_EXE=<path> dotnet test --project Kubism.Mcp.Tests`).
* Linux and Windows AOT builds come from the release workflow; they have not been run by hand.

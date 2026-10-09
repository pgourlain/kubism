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

## AOT status
* Our own code: `Kubism.Core` is built with `IsAotCompatible` and warnings as errors (zero trim/AOT warnings). JSON for our own types uses a source-generated context; Kubernetes objects are `JsonNode`s.
* `Kubism.Kube` suppresses a few IL warnings that originate in `KubernetesClient`; they are the thing the spike must settle.
* `spikes/aot-client` publishes with `-p:PublishAot=true`; run `spikes/aot-client/run.sh` on each OS and paste the result below. **Releases stay self-contained JIT builds until the spike is green on all three OSes.**

| OS | Result |
|---|---|
| macOS arm64 | see `spikes/aot-client/RESULT-macos.md` (filled by the script) |
| Linux x64 | pending: run on Ubuntu 24.04 |
| Windows x64 | pending |

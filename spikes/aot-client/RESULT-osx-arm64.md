# AOT spike result (osx-arm64, 2026-10, .NET 10.0.401 from Homebrew)

`dotnet publish -c Release -r osx-arm64 -p:PublishAot=true`:

* **Compile:** succeeds. Aggregate trim/AOT warnings come from two assemblies only: `KubernetesClient.dll` (IL2104, IL3053) and `YamlDotNet.dll` (IL2104, IL3053). `Kubism.Core` and the spike itself add none.
* **Link:** fails on this machine with `ld: library 'ssl' not found` (`-lssl -lcrypto` added to the clang line by the NativeAOT targets; Homebrew's .NET has no OpenSSL dev libs on the default linker path). The runtime scenario (list pods, watch, exec WebSocket) is therefore **not yet exercised in AOT**.

Next step (needs a human on each OS): run `spikes/aot-client/run.sh` on a machine with the official SDK (macOS arm64, Ubuntu 24.04 x64, Windows 11 x64) and record the output here. Until that is green, release packages are self-contained JIT builds (`packaging/package.sh`).

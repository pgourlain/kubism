Unsigned builds for Windows, macOS (Apple Silicon and Intel) and Linux. Kubism needs no `kubectl`; it reads your kubeconfig files (`KUBECONFIG`, `~/.kube/config`). For AKS/EKS/GKE, the exec plugin of your kubeconfig (`kubelogin`, `aws`, `gke-gcloud-auth-plugin`) must be installed.

- **Windows**: unzip `Kubism-<version>-win-x64.zip` and run `Kubism.exe` (SmartScreen may warn: More info → Run anyway).
- **macOS**: open the `.dmg` and drag `Kubism.app` to Applications (`osx-arm64` for Apple Silicon, `osx-x64` for Intel). The app is not notarized, so macOS blocks the first launch: open System Settings › Privacy & Security and click **Open Anyway**, or run `xattr -dr com.apple.quarantine /Applications/Kubism.app`. The right-click → Open trick no longer works on macOS 15 and later.
- **Linux**: extract `Kubism-<version>-linux-x64.tar.gz`; the app is in `kubism-<version>/opt/kubism/` (run `./Kubism`), and `usr/share/applications/kubism.desktop` can be copied to `~/.local/share/applications/` for a menu entry.
- **Arch / Omarchy**: `sudo pacman -U kubism-bin-<version>-1-x86_64.pkg.tar.zst`.

To try it without a cluster: `Kubism --demo` starts a fake API server with sample clusters. `Kubism --mcp` starts the MCP server for AI assistants (see `docs/mcp.md`).

#!/usr/bin/env bash
# Publishes the spike with Native AOT, runs it against the fake API server and records the result.
# Usage: spikes/aot-client/run.sh [rid]   (default: current OS/arch)
set -euo pipefail
cd "$(dirname "$0")/../.."
RID="${1:-$(dotnet --info | awk '/RID:/{print $2}')}"
OUT="spikes/aot-client/bin/aot-$RID"
dotnet publish spikes/aot-client -c Release -r "$RID" -o "$OUT" -p:PublishAot=true 2>&1 | tee "spikes/aot-client/publish-$RID.log" | tail -5
dotnet build spikes/fake-server -c Release -o spikes/fake-server/bin >/dev/null
coproc FAKE { dotnet spikes/fake-server/bin/fake-server.dll; }
read -r KUBECONFIG_PATH <&"${FAKE[0]}"
RESULT="spikes/aot-client/RESULT-$RID.md"
{
  echo "# AOT spike result ($RID, $(date -u +%F))"
  echo
  echo "Warnings in publish log: $(grep -c 'warning' "spikes/aot-client/publish-$RID.log" || true)"
  echo
  echo '```'
  "$OUT/aot-client" "$KUBECONFIG_PATH" 5 2>&1 || echo "FAILED (exit $?)"
  echo '```'
} | tee "$RESULT"
kill "$FAKE_PID" 2>/dev/null || true

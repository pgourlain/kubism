#!/usr/bin/env bash
# Lists every Mach-O file of a macOS app bundle with its architectures, and fails if one lacks <arch> or carries another.
#   packaging/check-macho-archs.sh <App.app> <arch: arm64|x86_64>
# macOS shows its "Intel-based app / will not work with a future macOS" warning when the main executable, or any Mach-O the app loads or spawns,
# has no arm64 slice (Apple support article 102527; Rosetta ends after macOS 27). An arm64 package therefore has to hold arm64-only files,
# and an x86_64 package x86_64-only files. Non-Mach-O files (managed assemblies, resources) are not looked at.
set -euo pipefail
APP="${1:?path to the .app}"; ARCH="${2:?architecture: arm64 or x86_64}"
case "$ARCH" in arm64|x86_64) ;; *) echo "architecture must be arm64 or x86_64, not '$ARCH'" >&2; exit 2 ;; esac
[[ -d "$APP" ]] || { echo "$APP is not a directory" >&2; exit 2; }
command -v lipo >/dev/null || { echo "lipo not found: this check runs on macOS" >&2; exit 2; }

count=0; bad=0
while IFS= read -r -d '' f; do
  file -b "$f" | grep -q '^Mach-O' || continue
  count=$((count + 1))
  archs="$(lipo -archs "$f" 2>/dev/null || true)"
  rel="${f#"$APP"/}"
  if [[ "$archs" == "$ARCH" ]]; then
    printf 'ok    %-8s %s\n' "$archs" "$rel"
  else
    printf 'FAIL  %-8s %s  (expected only %s)\n' "${archs:-?}" "$rel" "$ARCH"
    bad=$((bad + 1))
  fi
done < <(find "$APP" -type f -print0 | sort -z)

if [[ "$count" -eq 0 ]]; then echo "no Mach-O file found in $APP" >&2; exit 1; fi
if [[ "$bad" -gt 0 ]]; then echo "$bad of $count Mach-O file(s) are not $ARCH only" >&2; exit 1; fi
echo "all $count Mach-O file(s) are $ARCH only"

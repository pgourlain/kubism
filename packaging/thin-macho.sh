#!/usr/bin/env bash
# Keeps only the target architecture in every Mach-O file of a macOS app bundle (NuGet ships libAvaloniaNative, libSkiaSharp and libHarfBuzzSharp as
# universal binaries and neither Avalonia nor `dotnet publish` can thin them). Fails when a file has no slice for the target.
#   packaging/thin-macho.sh <App.app> <arch: arm64|x86_64>
# lipo invalidates signatures: sign the bundle after this, never before.
set -euo pipefail
APP="${1:?path to the .app}"; ARCH="${2:?architecture: arm64 or x86_64}"
case "$ARCH" in arm64|x86_64) ;; *) echo "architecture must be arm64 or x86_64, not '$ARCH'" >&2; exit 2 ;; esac
command -v lipo >/dev/null || { echo "lipo not found: this runs on macOS" >&2; exit 2; }

thinned=0
while IFS= read -r -d '' f; do
  file -b "$f" | grep -q '^Mach-O' || continue
  archs="$(lipo -archs "$f")"
  [[ "$archs" == "$ARCH" ]] && continue
  if [[ " $archs " != *" $ARCH "* ]]; then
    echo "error: ${f#"$APP"/} has no $ARCH slice (it has: $archs)" >&2
    exit 1
  fi
  tmp="$f.thin"
  lipo -thin "$ARCH" "$f" -output "$tmp"
  cat "$tmp" > "$f"      # keeps the file's mode (executable bit) and extended attributes
  rm -f "$tmp"
  echo "thinned ${f#"$APP"/}: $archs -> $ARCH"
  thinned=$((thinned + 1))
done < <(find "$APP" -type f -print0)
echo "thinned $thinned file(s) to $ARCH"

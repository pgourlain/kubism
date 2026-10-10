#!/usr/bin/env bash
# Self-contained package for one runtime into artifacts/.
#   packaging/package.sh <rid: win-x64|linux-x64|osx-arm64|osx-x64> <version>
# Native AOT by default (docs/adr/0001-kubernetes-client.md); AOT=0 gives a self-contained JIT build.
set -euo pipefail
RID="${1:?rid}"; VERSION="${2:-0.1.0}"
cd "$(dirname "$0")/.."
# The macOS packages are native for their architecture: an osx-arm64 build runs on an arm64 dotnet, never an x64 one under Rosetta.
if [[ "$RID" == osx-arm64 && "$(uname -s)" == Darwin ]]; then
  if [[ "$(uname -m)" != arm64 || "$(sysctl -n sysctl.proc_translated 2>/dev/null || echo 0)" == 1 ]]; then
    echo "error: osx-arm64 must be packaged natively on Apple silicon (this shell is Intel or runs under Rosetta)" >&2; exit 1
  fi
  if dotnet --info | grep -qE '^[[:space:]]*RID:[[:space:]]+osx-x64'; then
    echo "error: the dotnet SDK on PATH is x64; install the arm64 SDK" >&2; exit 1
  fi
fi
OUT="artifacts"; PUB="$OUT/publish-$RID"
rm -rf "$PUB"; mkdir -p "$OUT"
NOTICES() { cp LICENSE THIRD-PARTY-NOTICES.txt "$1/"; rm -rf "$1/third-party"; cp -R third-party "$1/third-party"; }
# Native AOT is the default (csproj); AOT=0 builds a self-contained JIT package instead.
AOT_ARGS=(); [[ "${AOT:-1}" == "0" ]] && AOT_ARGS=(-p:PublishAot=false)
dotnet publish Kubism.Desktop -c Release -r "$RID" -p:Version="$VERSION" ${AOT_ARGS[@]+"${AOT_ARGS[@]}"} -o "$PUB"

find "$PUB" \( -name "*.pdb" -o -name "*.dSYM" -o -name "*.dbg" \) -prune -exec rm -rf {} + 2>/dev/null || true
NOTICES "$PUB"
case "$RID" in
  win-*)
    (cd "$PUB" && (command -v zip >/dev/null && zip -qr "../Kubism-$VERSION-$RID.zip" . || powershell -NoProfile -Command "Compress-Archive -Path '*' -DestinationPath '../Kubism-$VERSION-$RID.zip' -Force"))
    ;;
  osx-*)
    APP="$OUT/Kubism.app"
    case "$RID" in osx-arm64) ARCH=arm64 ;; osx-x64) ARCH=x86_64 ;; *) echo "unknown macOS runtime $RID" >&2; exit 1 ;; esac
    # Only the arm64 package states an architecture priority (there is no Rosetta option, ever); the Intel package is Intel by construction.
    ARCH_PRIORITY=""; [[ "$ARCH" == arm64 ]] && ARCH_PRIORITY='<key>LSArchitecturePriority</key><array><string>arm64</string></array>'
    rm -rf "$APP"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    cp -R "$PUB"/. "$APP/Contents/MacOS/"
    # NuGet's native libraries are universal binaries: keep only this package's architecture (before signing: lipo invalidates signatures).
    packaging/thin-macho.sh "$APP" "$ARCH"
    cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Kubism</string>
  <key>CFBundleDisplayName</key><string>Kubism</string>
  <key>CFBundleIdentifier</key><string>io.github.kubism</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleExecutable</key><string>Kubism</string>
  <key>CFBundleIconFile</key><string>Kubism.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  $ARCH_PRIORITY
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
    cp Kubism.Desktop/Assets/Kubism.icns "$APP/Contents/Resources/Kubism.icns"
    chmod +x "$APP/Contents/MacOS/Kubism"
    # Apple Silicon refuses to run unsigned code: an ad-hoc signature is enough (the app is not notarized).
    if command -v codesign >/dev/null; then
      codesign --force --deep --sign - "$APP"
      codesign --verify --deep --strict "$APP"
    fi
    packaging/check-macho-archs.sh "$APP" "$ARCH"
    # Disk image: the app, a shortcut to Applications (drag and drop install) and the first-launch instructions.
    # hdiutil is flaky on CI runners ("resource busy"): retry, then fall back to a ditto zip.
    made=0
    if command -v hdiutil >/dev/null; then
      DMG="$OUT/dmg-$RID"
      rm -rf "$DMG"; mkdir -p "$DMG"
      ditto "$APP" "$DMG/Kubism.app"
      ln -s /Applications "$DMG/Applications"
      cp packaging/macos/ReadMeFirst.txt "$DMG/Read Me First.txt"
      for try in 1 2 3; do
        if hdiutil create -quiet -volname "Kubism" -srcfolder "$DMG" -ov -format UDZO "$OUT/Kubism-$VERSION-$RID.dmg"; then made=1; break; fi
        sleep 5
      done
      rm -rf "$DMG"
    fi
    if [[ "$made" == "0" ]]; then
      echo "dmg creation failed; shipping a zip instead"
      ditto -c -k --keepParent "$APP" "$OUT/Kubism-$VERSION-$RID.zip"
    fi
    ;;
  linux-*)
    STAGE="$OUT/kubism-$VERSION"
    rm -rf "$STAGE"; mkdir -p "$STAGE/opt/kubism" "$STAGE/usr/share/applications"
    cp -R "$PUB"/. "$STAGE/opt/kubism/"
    cp Kubism.Desktop/Assets/kubism.png "$STAGE/opt/kubism/kubism.png"
    cp Kubism.Desktop/Assets/kubism.png "$STAGE/usr/share/kubism.png" 2>/dev/null || true
    cp packaging/kubism.desktop "$STAGE/usr/share/applications/kubism.desktop"
    cp packaging/arch/PKGBUILD "$OUT/PKGBUILD"
    tar -C "$OUT" -czf "$OUT/Kubism-$VERSION-$RID.tar.gz" "kubism-$VERSION"
    ;;
esac
echo "Packaged:"; ls -1 "$OUT" | grep -E "Kubism-$VERSION|PKGBUILD" || true

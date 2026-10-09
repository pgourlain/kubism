#!/usr/bin/env bash
# Self-contained package for one runtime into artifacts/.
#   packaging/package.sh <rid: win-x64|linux-x64|osx-arm64|osx-x64> <version>
# Native AOT is not used yet (see docs/adr/0001-kubernetes-client.md); set AOT=1 to try it.
set -euo pipefail
RID="${1:?rid}"; VERSION="${2:-0.1.0}"
cd "$(dirname "$0")/.."
OUT="artifacts"; PUB="$OUT/publish-$RID"
rm -rf "$PUB"; mkdir -p "$OUT"
AOT_ARGS=(); [[ "${AOT:-0}" == "1" ]] && AOT_ARGS=(-p:PublishAot=true) || AOT_ARGS=(-p:PublishSingleFile=false)
dotnet publish Kubism.Desktop -c Release -r "$RID" --self-contained -p:Version="$VERSION" -p:DebugType=none "${AOT_ARGS[@]}" -o "$PUB"

case "$RID" in
  win-*)
    (cd "$PUB" && (command -v zip >/dev/null && zip -qr "../Kubism-$VERSION-$RID.zip" . || powershell -NoProfile -Command "Compress-Archive -Path '*' -DestinationPath '../Kubism-$VERSION-$RID.zip' -Force"))
    ;;
  osx-*)
    APP="$OUT/Kubism.app"
    rm -rf "$APP"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    cp -R "$PUB"/. "$APP/Contents/MacOS/"
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
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
    chmod +x "$APP/Contents/MacOS/Kubism"
    if command -v hdiutil >/dev/null; then
      hdiutil create -quiet -volname Kubism -srcfolder "$APP" -ov -format UDZO "$OUT/Kubism-$VERSION-$RID.dmg"
    else
      (cd "$OUT" && zip -qr "Kubism-$VERSION-$RID.zip" Kubism.app)
    fi
    ;;
  linux-*)
    STAGE="$OUT/kubism-$VERSION"
    rm -rf "$STAGE"; mkdir -p "$STAGE/opt/kubism" "$STAGE/usr/share/applications"
    cp -R "$PUB"/. "$STAGE/opt/kubism/"
    cp packaging/kubism.desktop "$STAGE/usr/share/applications/kubism.desktop"
    cp packaging/PKGBUILD "$OUT/PKGBUILD" 2>/dev/null || true
    tar -C "$OUT" -czf "$OUT/Kubism-$VERSION-$RID.tar.gz" "kubism-$VERSION"
    ;;
esac
echo "Packaged:"; ls -1 "$OUT" | grep -E "Kubism-$VERSION|PKGBUILD" || true

#!/usr/bin/env bash
set -euo pipefail

RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${2:-$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -n 1)}"
PUBLISH="$ROOT/dist/macos/$RID/publish"
APP="$ROOT/dist/macos/$RID/ZFTP.app"

if [[ "$RID" != "osx-arm64" && "$RID" != "osx-x64" ]]; then
  echo "Usage: $0 [osx-arm64|osx-x64]" >&2
  exit 2
fi

if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Could not determine a valid ZFTP version." >&2
  exit 2
fi

dotnet publish "$ROOT/src/ZFTP.Mac/ZFTP.Mac.csproj" -c Release -r "$RID" --self-contained true \
  -p:Version="$VERSION" -p:FileVersion="$VERSION.0" -p:AssemblyVersion="$VERSION.0" -o "$PUBLISH"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH"/. "$APP/Contents/MacOS/"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>ZFTP</string>
  <key>CFBundleDisplayName</key><string>ZFTP</string>
  <key>CFBundleIdentifier</key><string>xyz.zftp.app</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleExecutable</key><string>ZFTP</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

chmod +x "$APP/Contents/MacOS/ZFTP"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$ROOT/dist/ZFTP-macOS-${RID#osx-}.zip"
echo "Created $ROOT/dist/ZFTP-macOS-${RID#osx-}.zip"

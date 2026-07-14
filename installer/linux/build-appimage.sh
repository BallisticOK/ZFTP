#!/usr/bin/env bash
# ============================================================================
#  Builds ZFTP-<version>-x86_64.AppImage from a self-contained linux-x64
#  publish of ZFTP.App.
#
#  Requires: dotnet SDK, and either a local `appimagetool` on PATH or internet
#  access to download it (https://github.com/AppImage/appimagetool). Must run
#  on an actual Linux host (or a Linux container/VM) - AppImage tooling is a
#  native Linux ELF binary, it cannot run under Windows/WSL-without-a-distro.
#
#  Usage: installer/linux/build-appimage.sh [version]
# ============================================================================
set -euo pipefail

VERSION="${1:-0.0.0}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BUILD_DIR="$(mktemp -d)"
APPDIR="$BUILD_DIR/ZFTP.AppDir"
trap 'rm -rf "$BUILD_DIR"' EXIT

echo "==> Publishing ZFTP.App + zftpd (linux-x64, self-contained)"
dotnet publish "$ROOT_DIR/src/ZFTP.App/ZFTP.App.csproj" \
    -c Release -f net8.0 -r linux-x64 --self-contained true \
    -o "$APPDIR/usr/bin"
dotnet publish "$ROOT_DIR/src/ZFTP.Daemon/ZFTP.Daemon.csproj" \
    -c Release -f net8.0 -r linux-x64 --self-contained true \
    -o "$APPDIR/usr/bin"

echo "==> Assembling AppDir"
mkdir -p "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"
cp "$ROOT_DIR/installer/linux/zftp.desktop" "$APPDIR/usr/share/applications/zftp.desktop"
cp "$ROOT_DIR/installer/linux/zftp.desktop" "$APPDIR/zftp.desktop"
cp "$ROOT_DIR/src/ZFTP.App/Assets/logo.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/zftp.png"
cp "$ROOT_DIR/src/ZFTP.App/Assets/logo.png" "$APPDIR/zftp.png"
cp "$ROOT_DIR/installer/linux/AppRun" "$APPDIR/AppRun"
chmod +x "$APPDIR/AppRun" "$APPDIR/usr/bin/ZFTP" "$APPDIR/usr/bin/zftpd"

APPIMAGETOOL="$BUILD_DIR/appimagetool"
if command -v appimagetool >/dev/null 2>&1; then
    APPIMAGETOOL="$(command -v appimagetool)"
else
    echo "==> Downloading appimagetool"
    curl -fL -o "$APPIMAGETOOL" \
        https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
    chmod +x "$APPIMAGETOOL"
fi

mkdir -p "$ROOT_DIR/dist"
OUT="$ROOT_DIR/dist/ZFTP-$VERSION-x86_64.AppImage"
echo "==> Building AppImage"
ARCH=x86_64 "$APPIMAGETOOL" "$APPDIR" "$OUT"

echo "==> Done: $OUT"

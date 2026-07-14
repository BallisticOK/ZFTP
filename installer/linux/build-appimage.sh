#!/usr/bin/env bash
# ============================================================================
#  Builds ZFTP-<version>-<arch>.AppImage from a self-contained publish of
#  ZFTP.App + zftpd.
#
#  Requires: dotnet SDK, and either a local `appimagetool` on PATH or internet
#  access to download it (https://github.com/AppImage/appimagetool). Must run
#  on an actual Linux host (or a Linux container/VM) - AppImage tooling is a
#  native Linux ELF binary, it cannot run under Windows/WSL-without-a-distro.
#
#  Usage: installer/linux/build-appimage.sh [version] [rid]
#  rid defaults to linux-x64; linux-arm64 is cross-compiled (a
#  framework-dependent publish needs no arm64 hardware to build, only to run)
#  - appimagetool itself still runs as whatever arch the BUILD machine is,
#  the ARCH env var just controls the output AppImage's target/filename.
# ============================================================================
set -euo pipefail

VERSION="${1:-0.0.0}"
RID="${2:-linux-x64}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BUILD_DIR="$(mktemp -d)"
APPDIR="$BUILD_DIR/ZFTP.AppDir"
trap 'rm -rf "$BUILD_DIR"' EXIT

case "$RID" in
    linux-x64)   APPIMAGE_ARCH=x86_64 ;;
    linux-arm64) APPIMAGE_ARCH=aarch64 ;;
    *) echo "Unsupported RID for AppImage: $RID" >&2; exit 1 ;;
esac

echo "==> Publishing ZFTP.App + zftpd ($RID, self-contained)"
dotnet publish "$ROOT_DIR/src/ZFTP.App/ZFTP.App.csproj" \
    -c Release -f net8.0 -r "$RID" --self-contained true \
    -o "$APPDIR/usr/bin"
dotnet publish "$ROOT_DIR/src/ZFTP.Daemon/ZFTP.Daemon.csproj" \
    -c Release -f net8.0 -r "$RID" --self-contained true \
    -o "$APPDIR/usr/bin"

echo "==> Assembling AppDir"
mkdir -p "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"
cp "$ROOT_DIR/installer/linux/zftp.desktop" "$APPDIR/usr/share/applications/zftp.desktop"
cp "$ROOT_DIR/installer/linux/zftp.desktop" "$APPDIR/zftp.desktop"
cp "$ROOT_DIR/src/ZFTP.App/Assets/logo.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/zftp.png"
cp "$ROOT_DIR/src/ZFTP.App/Assets/logo.png" "$APPDIR/zftp.png"
cp "$ROOT_DIR/installer/linux/AppRun" "$APPDIR/AppRun"
chmod +x "$APPDIR/AppRun" "$APPDIR/usr/bin/ZFTP" "$APPDIR/usr/bin/zftpd"

# appimagetool itself must match the BUILD machine's architecture (it's what
# actually executes here) regardless of which arch we're packaging for.
BUILD_ARCH="$(uname -m)"
APPIMAGETOOL="$BUILD_DIR/appimagetool"
if command -v appimagetool >/dev/null 2>&1; then
    APPIMAGETOOL="$(command -v appimagetool)"
else
    echo "==> Downloading appimagetool ($BUILD_ARCH)"
    curl -fL -o "$APPIMAGETOOL" \
        "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$BUILD_ARCH.AppImage"
    chmod +x "$APPIMAGETOOL"
fi

mkdir -p "$ROOT_DIR/dist"
OUT="$ROOT_DIR/dist/ZFTP-$VERSION-$APPIMAGE_ARCH.AppImage"
echo "==> Building AppImage"
ARCH="$APPIMAGE_ARCH" "$APPIMAGETOOL" "$APPDIR" "$OUT"

echo "==> Done: $OUT"

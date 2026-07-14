#!/usr/bin/env bash
# ============================================================================
#  Builds ZFTP-<version>-<rid>.tar.gz: a self-contained publish of both
#  ZFTP.App (the GUI) and zftpd (the headless CLI/daemon), in one
#  extract-and-run folder - no AppImage tooling needed, works everywhere.
#
#  Usage: installer/linux/build-tarball.sh [version] [rid]
#  rid defaults to linux-x64; linux-arm64 is cross-compiled the same way (a
#  framework-dependent publish needs no arm64 hardware to build, only to run).
# ============================================================================
set -euo pipefail

VERSION="${1:-0.0.0}"
RID="${2:-linux-x64}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BUILD_DIR="$(mktemp -d)"
OUT_DIR="$BUILD_DIR/ZFTP"
trap 'rm -rf "$BUILD_DIR"' EXIT

echo "==> Publishing ZFTP.App + zftpd ($RID, self-contained)"
dotnet publish "$ROOT_DIR/src/ZFTP.App/ZFTP.App.csproj" \
    -c Release -f net8.0 -r "$RID" --self-contained true -o "$OUT_DIR"
dotnet publish "$ROOT_DIR/src/ZFTP.Daemon/ZFTP.Daemon.csproj" \
    -c Release -f net8.0 -r "$RID" --self-contained true -o "$OUT_DIR"
chmod +x "$OUT_DIR/ZFTP" "$OUT_DIR/zftpd"

mkdir -p "$ROOT_DIR/dist"
OUT="$ROOT_DIR/dist/ZFTP-$VERSION-$RID.tar.gz"
echo "==> Archiving"
tar --owner=0 --group=0 --mode=755 -C "$BUILD_DIR" -czf "$OUT" ZFTP

echo "==> Done: $OUT"

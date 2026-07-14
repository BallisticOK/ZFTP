#!/usr/bin/env bash
# ============================================================================
#  Builds ZFTP-<version>-linux-x64.tar.gz: a self-contained linux-x64 publish
#  of both ZFTP.App (the GUI) and zftpd (the headless CLI/daemon), in one
#  extract-and-run folder - no AppImage tooling needed, works everywhere.
#
#  Usage: installer/linux/build-tarball.sh [version]
# ============================================================================
set -euo pipefail

VERSION="${1:-0.0.0}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BUILD_DIR="$(mktemp -d)"
OUT_DIR="$BUILD_DIR/ZFTP"
trap 'rm -rf "$BUILD_DIR"' EXIT

echo "==> Publishing ZFTP.App + zftpd (linux-x64, self-contained)"
dotnet publish "$ROOT_DIR/src/ZFTP.App/ZFTP.App.csproj" \
    -c Release -f net8.0 -r linux-x64 --self-contained true -o "$OUT_DIR"
dotnet publish "$ROOT_DIR/src/ZFTP.Daemon/ZFTP.Daemon.csproj" \
    -c Release -f net8.0 -r linux-x64 --self-contained true -o "$OUT_DIR"
chmod +x "$OUT_DIR/ZFTP" "$OUT_DIR/zftpd"

mkdir -p "$ROOT_DIR/dist"
OUT="$ROOT_DIR/dist/ZFTP-$VERSION-linux-x64.tar.gz"
echo "==> Archiving"
tar --owner=0 --group=0 --mode=755 -C "$BUILD_DIR" -czf "$OUT" ZFTP

echo "==> Done: $OUT"

#!/usr/bin/env bash
set -euo pipefail

REPO="${ZFTP_REPO:-BallisticOK/ZFTP}"
INSTALL_DIR="${ZFTP_INSTALL_DIR:-$HOME/.local/bin}"
REQUESTED_VERSION="${ZFTP_VERSION:-latest}"

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "ZFTP CLI installer: Linux is required." >&2
  exit 2
fi

case "$(uname -m)" in
  x86_64|amd64) ARCH="x64" ;;
  aarch64|arm64) ARCH="arm64" ;;
  *)
    echo "ZFTP CLI installer: unsupported CPU architecture: $(uname -m)" >&2
    exit 2
    ;;
esac

ASSET="zftp-linux-$ARCH"
if [[ "$REQUESTED_VERSION" == "latest" ]]; then
  BASE_URL="https://github.com/$REPO/releases/latest/download"
else
  VERSION="${REQUESTED_VERSION#v}"
  BASE_URL="https://github.com/$REPO/releases/download/v$VERSION"
fi

for command_name in curl sha256sum; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "ZFTP CLI installer: '$command_name' is required." >&2
    exit 1
  fi
done

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

echo "Downloading ZFTP CLI for Linux $ARCH..."
curl --fail --location --silent --show-error --retry 3 \
  "$BASE_URL/$ASSET" -o "$TMP_DIR/$ASSET"
curl --fail --location --silent --show-error --retry 3 \
  "$BASE_URL/SHA256SUMS" -o "$TMP_DIR/SHA256SUMS"

EXPECTED="$(awk -v asset="$ASSET" '$2 == asset { print $1; exit }' "$TMP_DIR/SHA256SUMS")"
if [[ -z "$EXPECTED" ]]; then
  echo "ZFTP CLI installer: no checksum was published for $ASSET." >&2
  exit 1
fi

ACTUAL="$(sha256sum "$TMP_DIR/$ASSET" | awk '{ print $1 }')"
if [[ "$ACTUAL" != "$EXPECTED" ]]; then
  echo "ZFTP CLI installer: checksum verification failed." >&2
  exit 1
fi

mkdir -p "$INSTALL_DIR"
install -m 0755 "$TMP_DIR/$ASSET" "$INSTALL_DIR/zftp"

echo "Installed ZFTP CLI to $INSTALL_DIR/zftp"

if [[ ":$PATH:" != *":$INSTALL_DIR:"* ]]; then
  echo
  echo "Add ZFTP to your PATH for future shells:"
  echo "  export PATH=\"$INSTALL_DIR:\$PATH\""
fi

if ! command -v rclone >/dev/null 2>&1 || \
   { ! command -v fusermount3 >/dev/null 2>&1 && ! command -v mount.fuse3 >/dev/null 2>&1; }; then
  echo
  echo "ZFTP mounts also need rclone and FUSE 3."
  if command -v apt-get >/dev/null 2>&1; then
    echo "  Debian/Ubuntu: sudo apt-get update && sudo apt-get install -y rclone fuse3"
  elif command -v dnf >/dev/null 2>&1; then
    echo "  Fedora/RHEL:    sudo dnf install -y rclone fuse3"
  elif command -v pacman >/dev/null 2>&1; then
    echo "  Arch Linux:     sudo pacman -S --needed rclone fuse3"
  elif command -v zypper >/dev/null 2>&1; then
    echo "  openSUSE:       sudo zypper install rclone fuse3"
  else
    echo "  Install the 'rclone' and 'fuse3' packages using your distribution's package manager."
  fi
fi

echo
echo "Next:"
echo "  zftp doctor"
echo "  zftp config"
echo
echo "Run this installer again at any time to upgrade to the latest release."

#!/usr/bin/env bash
set -euo pipefail

ROOTFS="${1:?usage: build-linux-rootfs.sh OUTPUT_ROOTFS}"
ARCHIVE="${2:?usage: build-linux-rootfs.sh OUTPUT_ROOTFS OUTPUT_ARCHIVE}"
ROOTFS="$(realpath -m "$ROOTFS")"
ARCHIVE="$(realpath -m "$ARCHIVE")"

sudo apt-get update
sudo apt-get install -y debootstrap ca-certificates curl
sudo rm -rf "$ROOTFS"
sudo mkdir -p "$ROOTFS"
sudo debootstrap --arch=amd64 --variant=minbase --include=apt,ca-certificates,gnupg bookworm "$ROOTFS" https://deb.debian.org/debian

sudo chroot "$ROOTFS" /usr/bin/env DEBIAN_FRONTEND=noninteractive apt-get update
sudo chroot "$ROOTFS" /usr/bin/env DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
  xvfb x11vnc novnc python3-websockify python3 \
  dbus-x11 xauth x11-utils xdotool \
  fonts-dejavu-core fontconfig \
  libfontconfig1 libfreetype6 libx11-6 libice6 libsm6 libxrender1 libxext6 \
  libxfixes3 libxi6 libxrandr2 libxss1 libxtst6 libglib2.0-0 \
  libgl1 libegl1 libxcb1 libxkbcommon0 libdbus-1-3 libgcc-s1 libstdc++6 \
  libx11-xcb1 libxcb-render0 libxcb-shape0 libxcb-randr0 libxcb-xfixes0 libxcb-sync1 \
  libxcb-xkb1 libxkbcommon-x11-0 libxcb-cursor0 libxcb-icccm4 libxcb-image0 \
  libxcb-keysyms1 libxcb-render-util0 libxcb-xinerama0 libharfbuzz0b libpng16-16 \
  zlib1g libssl3 libicu72
sudo chroot "$ROOTFS" /usr/bin/env DEBIAN_FRONTEND=noninteractive apt-get clean
sudo rm -rf "$ROOTFS/var/lib/apt/lists/"* "$ROOTFS/var/cache/apt/"*

curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
sudo mkdir -p "$ROOTFS/opt/dotnet"
sudo bash /tmp/dotnet-install.sh \
  --runtime dotnet --channel 8.0 --architecture x64 --os linux \
  --install-dir "$ROOTFS/opt/dotnet"

test -x "$ROOTFS/opt/dotnet/dotnet"
file "$ROOTFS/opt/dotnet/dotnet"
ldd "$ROOTFS/opt/dotnet/dotnet" | tee /tmp/uabea-dotnet-ldd.txt
if grep -q "not found" /tmp/uabea-dotnet-ldd.txt; then
  echo "The Linux x64 .NET runtime has unresolved shared libraries." >&2
  exit 1
fi

sudo mkdir -p "$ROOTFS/opt/uabea" "$ROOTFS/workspace" "$ROOTFS/usr/local/bin"
sudo chown -R "$(id -u):$(id -g)" "$ROOTFS/opt/dotnet" "$ROOTFS/opt/uabea" "$ROOTFS/workspace" "$ROOTFS/usr/local/bin"
sudo tar --numeric-owner --xattrs --acls -czf "$ARCHIVE" -C "$ROOTFS" .
gzip -t "$ARCHIVE"
tar -tzf "$ARCHIVE" | grep -E '^\./(opt/dotnet/dotnet|usr/bin/Xvfb|usr/bin/x11vnc|usr/bin/python3)$'
echo "Rootfs archive: $ARCHIVE ($(du -h "$ARCHIVE" | cut -f1))"

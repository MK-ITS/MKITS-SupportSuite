#!/usr/bin/env bash
# Installs the arm64 libjpeg-turbo on an amd64 Debian/Ubuntu build machine (dpkg multiarch) for cross builds of the
# arm64 AppImage and package (TARGET_ARCH=arm64). Ubuntu serves arm64 from ports.ubuntu.com, Debian from its mirrors.
set -euo pipefail
. /etc/os-release
dpkg --add-architecture arm64
if [ "$ID" = "ubuntu" ]; then
  # Pin the existing sources to amd64 and add the ports archive for arm64.
  for f in /etc/apt/sources.list.d/*.sources; do
    grep -q '^Architectures:' "$f" || sed -i '/^URIs:/i Architectures: amd64' "$f"
  done
  cat > /etc/apt/sources.list.d/ubuntu-ports-arm64.sources <<SOURCES
Types: deb
URIs: http://ports.ubuntu.com/ubuntu-ports/
Suites: $VERSION_CODENAME $VERSION_CODENAME-updates $VERSION_CODENAME-security
Components: main universe
Architectures: arm64
Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg
SOURCES
  package=libturbojpeg:arm64
else
  package=libturbojpeg0:arm64
fi
apt-get update
apt-get install -y --no-install-recommends "$package"
ls /usr/lib/aarch64-linux-gnu/libturbojpeg.so.0

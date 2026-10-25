#!/usr/bin/env bash
# Builds the Linux ad-hoc client as AppImage: artifacts/linux/MKITS-Support-x86_64.AppImage
#
# Requirements on the build machine: .NET SDK, libturbojpeg (bundled into the AppImage), curl.
# appimagetool is downloaded unless APPIMAGETOOL points to it. Without FUSE (CI, containers) set
# APPIMAGE_EXTRACT_AND_RUN=1.
#
# The application is published self-contained as a folder, not as a single file: the
# AppImage is mounted read-only, and libturbojpeg next to the assembly is found by the .NET library probing.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
out="${1:-$repo/artifacts/linux}"
build_number="${BUILD_NUMBER:-0}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

appdir="$work/AppDir"
mkdir -p "$appdir/usr/lib/mkits-agent" "$out"

dotnet publish "$repo/src/Agent/MKITS.SupportSuite.Agent.Linux/MKITS.SupportSuite.Agent.Linux.csproj" \
  -c Release -r linux-x64 --self-contained -p:BuildNumber="$build_number" -o "$appdir/usr/lib/mkits-agent"

turbojpeg="$(ldconfig -p | awk '/libturbojpeg\.so\.0 .*x86-64/ { print $NF; exit }')"
if [ -z "$turbojpeg" ]; then
  echo "libturbojpeg.so.0 not found – install libturbojpeg (Ubuntu) or libturbojpeg0 (Debian)" >&2
  exit 1
fi
cp -L "$turbojpeg" "$appdir/usr/lib/mkits-agent/libturbojpeg.so.0"

cat > "$appdir/AppRun" <<'APPRUN'
#!/bin/sh
here="$(dirname "$(readlink -f "$0")")"
exec "$here/usr/lib/mkits-agent/mkits-agent" "$@"
APPRUN
chmod +x "$appdir/AppRun"
cp "$repo/deploy/linux/mkits-support.desktop" "$appdir/"
cp "$repo/deploy/linux/mkits-support.png" "$appdir/"

tool="${APPIMAGETOOL:-}"
if [ -z "$tool" ]; then
  tool="$work/appimagetool"
  curl -fsSL -o "$tool" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
  chmod +x "$tool"
fi

ARCH=x86_64 "$tool" --no-appstream "$appdir" "$out/MKITS-Support-x86_64.AppImage"
echo "Built $out/MKITS-Support-x86_64.AppImage"

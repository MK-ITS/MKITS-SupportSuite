#!/usr/bin/env bash
# Builds the installed Linux agent as Debian package: artifacts/linux/mkits-agent_<version>_amd64.deb
#
# Requirements on the build machine: .NET SDK, dpkg-deb, libturbojpeg (bundled, as in the AppImage).
# The package contains the self-contained agent in /opt/mkits-agent, /usr/bin/mkits-agent and the systemd unit;
# it is connected to a server afterwards with "sudo mkits-agent --install --server … --token …" (ADR-0025).
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
out="${1:-$repo/artifacts/linux}"
build_number="${BUILD_NUMBER:-0}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

version_prefix="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$repo/Directory.Build.props")"
version="${version_prefix}+${build_number}"
root="$work/root"
app="$root/opt/mkits-agent"
mkdir -p "$app" "$root/usr/bin" "$root/usr/lib/systemd/system" "$root/DEBIAN" "$out"

dotnet publish "$repo/src/Agent/MKITS.SupportSuite.Agent.Linux/MKITS.SupportSuite.Agent.Linux.csproj" \
  -c Release -r linux-x64 --self-contained -p:BuildNumber="$build_number" -o "$app"

turbojpeg="$(ldconfig -p | awk '/libturbojpeg\.so\.0 .*x86-64/ { print $NF; exit }')"
if [ -z "$turbojpeg" ]; then
  echo "libturbojpeg.so.0 not found – install libturbojpeg (Ubuntu) or libturbojpeg0 (Debian)" >&2
  exit 1
fi
cp -L "$turbojpeg" "$app/libturbojpeg.so.0"

ln -s /opt/mkits-agent/mkits-agent "$root/usr/bin/mkits-agent"
install -m 0644 "$repo/deploy/linux/mkits-agent.service" "$root/usr/lib/systemd/system/mkits-agent.service"
install -m 0644 "$repo/LICENSE" "$app/LICENSE"
install -m 0644 "$repo/THIRD-PARTY-NOTICES.md" "$app/THIRD-PARTY-NOTICES.md"

installed_size="$(du -sk "$root" | cut -f1)"
sed -e "s/@VERSION@/$version/" -e "s/@SIZE@/$installed_size/" "$repo/deploy/linux/deb/control" > "$root/DEBIAN/control"
for script in postinst prerm postrm; do
  install -m 0755 "$repo/deploy/linux/deb/$script" "$root/DEBIAN/$script"
done

# Files root-owned, folders 0755, program files not writable by others.
find "$root" -type d -exec chmod 0755 {} +
chmod -R go-w "$root"
chmod 0755 "$app/mkits-agent"
dpkg-deb --root-owner-group --build "$root" "$out/mkits-agent_${version}_amd64.deb"
echo "Built $out/mkits-agent_${version}_amd64.deb"

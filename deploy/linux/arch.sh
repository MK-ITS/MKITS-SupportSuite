# Sourced by the Linux build scripts: target architecture from TARGET_ARCH (x64 = default, arm64).
# Sets: rid (.NET runtime id), deb_arch (Debian), appimage_arch (AppImage/uname), ld_arch (ldconfig -p tag).
case "${TARGET_ARCH:-x64}" in
  x64)   rid=linux-x64;   deb_arch=amd64; appimage_arch=x86_64;  ld_arch='x86-64' ;;
  arm64) rid=linux-arm64; deb_arch=arm64; appimage_arch=aarch64; ld_arch='AArch64' ;;
  *) echo "TARGET_ARCH must be x64 or arm64" >&2; exit 2 ;;
esac

# libjpeg-turbo of the target architecture (bundled; cross builds install the foreign package via multiarch).
# TURBOJPEG overrides the lookup.
find_turbojpeg() {
  local found="${TURBOJPEG:-$(ldconfig -p | awk -v arch="$ld_arch" '/libturbojpeg\.so\.0 / && index($0, arch) { print $NF; exit }')}"
  if [ -z "$found" ] || [ ! -e "$found" ]; then
    echo "libturbojpeg.so.0 for $deb_arch not found – install libturbojpeg (Ubuntu) or libturbojpeg0 (Debian) for that architecture" >&2
    exit 1
  fi
  echo "$found"
}

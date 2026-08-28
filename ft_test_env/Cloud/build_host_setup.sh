#!/bin/bash
# Provisioning for the ft NativeAOT BUILD HOST (ft-node-79, issue #45 - Android/Termux build).
#
# Unlike the e2e nodes this runs NONE of the lab services. It stands up a .NET SDK + Android-NDK
# cross-compile toolchain so `dotnet publish -r linux-bionic-arm64` can produce the Android/Termux
# (Bionic libc, aarch64) build of ft, which runs natively in Termux with no glibc wrapper.
#
# The immutable root (~2.8 GB, resets every boot) can't hold the SDK/NDK/NuGet caches, so everything
# heavy lives on the persistent data disk (SATA port 2, attached by VBoxManager for BuildHost nodes)
# mounted at $BUILD. Re-run safe: a reboot resets the root and re-runs this, but the data disk keeps the
# toolchain, so the multi-GB downloads happen only once.
set -e

BUILD=/var/lib/ftbuild
DOTNET_CHANNEL="10.0"
NDK_VER="r27c"                                   # NDK r23c+ is required; r27c is a current LTS
NDK_DIR="$BUILD/android-ndk-${NDK_VER}"
NDK_URL="https://dl.google.com/android/repository/android-ndk-${NDK_VER}-linux.zip"

echo "=== ft build-host provisioning starting ==="

##### 1. Mount the persistent data disk at $BUILD. VBox disk enumeration is not stable, so identify the data
#####    disk as the whole disk that is NOT the root's disk (never hard-code sda/sdb) - same trick as the
#####    QEMU-host block in setup_debian.sh.
ROOTDISK=$(lsblk -no PKNAME "$(findmnt -no SOURCE / 2>/dev/null)" 2>/dev/null | head -1)
DATADISK=""
for d in $(lsblk -dn -o NAME 2>/dev/null | grep -E '^(sd|vd|nvme)'); do
  [ "$d" = "$ROOTDISK" ] && continue
  DATADISK="/dev/$d"; break
done
if [ -z "$DATADISK" ]; then echo "FATAL: build host has no data disk"; exit 1; fi

blkid "$DATADISK" >/dev/null 2>&1 || mkfs.ext4 -F -q "$DATADISK"
mkdir -p "$BUILD"
grep -q "$BUILD" /etc/fstab || echo "$DATADISK $BUILD ext4 defaults,nofail 0 2" >> /etc/fstab
mountpoint -q "$BUILD" || mount "$DATADISK" "$BUILD"
chmod 777 "$BUILD"

##### 2. Redirect the apt cache onto the data disk so re-provisioning (each boot) doesn't overflow the tiny
#####    immutable root and doesn't re-download the same debs.
rm -rf /var/cache/apt/archives 2>/dev/null || true
mkdir -p "$BUILD/apt-cache/partial"
ln -sfn "$BUILD/apt-cache" /var/cache/apt/archives

apt-get update -y
DEBIAN_FRONTEND=noninteractive apt-get install -y git curl unzip file clang lld binutils zlib1g-dev

##### 3. Persisted environment for the build script: DOTNET_ROOT / NuGet cache / build output on the data
#####    disk, and the NDK's llvm toolchain on PATH (the NativeAOT bionic link step calls the NDK's aarch64
#####    clang + lld). $BUILD / $NDK_DIR are expanded now; \$PATH stays literal for runtime.
cat > "$BUILD/env.sh" <<EOF
export DOTNET_ROOT="$BUILD/dotnet"
export NUGET_PACKAGES="$BUILD/nuget"
export PATH="$BUILD/dotnet:$NDK_DIR/toolchains/llvm/prebuilt/linux-x86_64/bin:\$PATH"
EOF

##### 4. The on-demand build script (kept here so a fresh host has it without cloning first). Literal heredoc.
cat > "$BUILD/build-bionic.sh" <<'BBEOF'
#!/bin/bash
# Produce the Android/Termux (linux-bionic-arm64) NativeAOT build of ft.
# Usage: build-bionic.sh [git-ref]   (default branch: android-termux-build)
set -e
BUILD=/var/lib/ftbuild
source "$BUILD/env.sh"
REF="${1:-android-termux-build}"
REPO="https://github.com/fiddyschmitt/File-Tunnel.git"
SRC="$BUILD/src"

[ -d "$SRC/.git" ] || git clone "$REPO" "$SRC"
cd "$SRC"
git fetch --all --tags --prune
git checkout -f "$REF"
git reset --hard "origin/$REF" 2>/dev/null || git reset --hard "$REF"

echo "=== publishing ft for linux-bionic-arm64 (NativeAOT) from $(git rev-parse --short HEAD) ==="
"$BUILD/dotnet/dotnet" publish ft/ft.csproj \
  -r linux-bionic-arm64 -c Release \
  -p:PublishAot=true -p:DisableUnsupportedError=true -p:PublishAotUsingRuntimePack=true

OUT="$SRC/ft/bin/Release/net10.0/linux-bionic-arm64/publish/ft"
cp -f "$OUT" "$BUILD/ft-linux-bionic-arm64"
echo "=== artifact ==="
file "$BUILD/ft-linux-bionic-arm64"
ls -la "$BUILD/ft-linux-bionic-arm64"
echo "ARTIFACT_OK: $BUILD/ft-linux-bionic-arm64"
BBEOF
chmod +x "$BUILD/build-bionic.sh"

##### 5. Readiness sentinel EARLY: the SDK+NDK download below is multi-GB. The node is usable now; the caller
#####    polls $BUILD/tools-ready (or tools-failed) before invoking build-bionic.sh.
touch /run/ft-setup-complete

##### 6. Install the .NET SDK + Android NDK onto the data disk - ONCE (idempotent; persists across the root
#####    reset). Runs DETACHED so cloud-init / node-readiness is not blocked by the download.
cat > "$BUILD/install-toolchain.sh" <<EOF
#!/bin/bash
set -e
trap 'touch "$BUILD/tools-failed"' ERR
rm -f "$BUILD/tools-ready" "$BUILD/tools-failed"
if [ ! -x "$BUILD/dotnet/dotnet" ]; then
  echo "installing .NET SDK ($DOTNET_CHANNEL) ..."
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$BUILD/dotnet-install.sh"
  bash "$BUILD/dotnet-install.sh" --channel "$DOTNET_CHANNEL" --install-dir "$BUILD/dotnet"
fi
if [ ! -d "$NDK_DIR" ]; then
  echo "downloading Android NDK ($NDK_VER) ..."
  curl -fsSL "$NDK_URL" -o "$BUILD/ndk.zip"
  unzip -q "$BUILD/ndk.zip" -d "$BUILD"
  rm -f "$BUILD/ndk.zip"
fi
touch "$BUILD/tools-ready"
echo "toolchain ready."
EOF
chmod +x "$BUILD/install-toolchain.sh"
setsid bash "$BUILD/install-toolchain.sh" >"$BUILD/toolchain-install.log" 2>&1 </dev/null &

echo "=== build-host provisioning done; toolchain installing in background (see $BUILD/toolchain-install.log) ==="

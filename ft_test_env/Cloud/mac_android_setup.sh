#!/bin/bash
# Set up the Android emulator toolchain on the Mac for the ft Android (linux-bionic-arm64) e2e client rows
# (issue #45). Installs cmdline-tools + platform-tools + emulator + an arm64-v8a system image into the
# user-owned ~/Library/Android/sdk, then creates the AVD. Idempotent (safe to re-run - the big downloads
# happen once). Uses Homebrew's android-commandlinetools as the bootstrap sdkmanager + Homebrew openjdk.
# Everything lands in ~/Library/Android/sdk, isolated from any other SDK/AVD already on the Mac.
#
# Args: $1 = AVD name (default ft_android), $2 = system image (default android-34 default arm64-v8a).
set -e
AVD="${1:-ft_android}"
IMAGE="${2:-system-images;android-34;default;arm64-v8a}"

export JAVA_HOME=/opt/homebrew/opt/openjdk@21
export ANDROID_SDK_ROOT="$HOME/Library/Android/sdk"
export ANDROID_HOME="$ANDROID_SDK_ROOT"
BOOTSTRAP=/opt/homebrew/share/android-commandlinetools/cmdline-tools/latest/bin/sdkmanager

if [ ! -x "$BOOTSTRAP" ]; then echo "FATAL: bootstrap sdkmanager missing ($BOOTSTRAP) - brew install --cask android-commandlinetools"; exit 1; fi
if [ ! -d "$JAVA_HOME" ]; then echo "FATAL: openjdk missing ($JAVA_HOME) - brew install openjdk@21"; exit 1; fi

mkdir -p "$ANDROID_SDK_ROOT"
echo "=== accepting licenses ==="
yes | "$BOOTSTRAP" --sdk_root="$ANDROID_SDK_ROOT" --licenses >/dev/null 2>&1 || true

# Install cmdline-tools INTO our SDK root as well: avdmanager derives the SDK root from its OWN location
# (toolsdir), ignoring ANDROID_SDK_ROOT, so our avdmanager must live under our root to find our image.
echo "=== installing cmdline-tools + platform-tools + emulator + $IMAGE ==="
"$BOOTSTRAP" --sdk_root="$ANDROID_SDK_ROOT" "cmdline-tools;latest" "platform-tools" "emulator" "$IMAGE"

AVDMGR="$ANDROID_SDK_ROOT/cmdline-tools/latest/bin/avdmanager"
echo "=== creating AVD '$AVD' ==="
if "$AVDMGR" list avd 2>/dev/null | grep -q "Name: $AVD"; then
  echo "AVD $AVD already exists"
else
  echo no | "$AVDMGR" create avd -n "$AVD" -k "$IMAGE" --force
fi

# Stage a real Bionic OpenSSL for ft's crypto backends (S3 SigV4, HTTPS/Dropbox). Android ships BoringSSL,
# which lacks the OpenSSL symbols .NET binds ("a2d_ASN1_OBJECT"); AndroidProcessRunner pushes these onto the
# emulator and runs ft with LD_LIBRARY_PATH pointed at them. From Termux's openssl package (Bionic-built).
# The UNVERSIONED sonames (libssl.so / libcrypto.so) matter - .NET's linux-bionic shim probes the bare soname.
OSSL_DIR="$HOME/Library/Android/ft-openssl"
OSSL_VER="1%3A3.6.3"
if [ ! -f "$OSSL_DIR/libssl.so" ] || [ ! -f "$OSSL_DIR/libcrypto.so" ]; then
  echo "=== staging Bionic OpenSSL (Termux openssl $OSSL_VER) ==="
  mkdir -p "$OSSL_DIR"
  TMPD=$(mktemp -d)
  ( cd "$TMPD"
    curl -sL -o o.deb "https://packages.termux.dev/apt/termux-main/pool/main/o/openssl/openssl_${OSSL_VER}_aarch64.deb"
    tar xf o.deb
    for f in data.tar.xz data.tar.zst data.tar.gz data.tar; do [ -f "$f" ] && tar xf "$f" 2>/dev/null && break; done
    L="data/data/com.termux/files/usr/lib"
    cp -f "$L/libcrypto.so.3" "$OSSL_DIR/libcrypto.so"
    cp -f "$L/libssl.so.3"    "$OSSL_DIR/libssl.so" )
  rm -rf "$TMPD"
fi
ls -la "$OSSL_DIR" 2>/dev/null | grep -E "libssl|libcrypto" || echo "WARNING: OpenSSL not staged (crypto backends will fail on the emulator)"
echo SETUP_DONE

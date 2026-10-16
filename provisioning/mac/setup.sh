#!/bin/bash
# File Tunnel test lab: the Mac's setup. `.\mac.ps1 setup` runs it on the Mac. Safe to repeat: the
# big downloads happen once.
#
#   * the Android emulator: the SDK's command-line tools, platform tools and an arm64-v8a system image
#     in ~/Library/Android/sdk, the AVD, and the emulator itself: the lab's own pinned copy
#   * what the Android rows push into the emulators: a real Bionic OpenSSL (for ft's S3 and Dropbox
#     backends) and Termux's sshfs (for the sshfs rows)
#   * the Mac's SMB share (~/ftshare, as `ftshare`) and the account that logs in to it
#   * the folders the tests mount the other machines' shares under
#
# Inputs (environment, from mac.ps1): FT_AVD, FT_IMAGE (the system image), FT_EMU_VERSION,
# FT_EMU_ZIP and FT_EMU_SHA256 (the emulator), FT_SMB_USER, FT_SMB_PASSWORD. Needs Homebrew, and sudo
# without a password (for the SMB account and share).
set -euo pipefail
export PATH="/opt/homebrew/bin:/opt/homebrew/sbin:/usr/local/bin:$PATH"
: "${FT_AVD:?}" "${FT_IMAGE:?}" "${FT_EMU_VERSION:?}" "${FT_EMU_ZIP:?}" "${FT_EMU_SHA256:?}"
: "${FT_SMB_USER:?}" "${FT_SMB_PASSWORD:?}"

command -v brew >/dev/null || { echo "Homebrew is needed first: https://brew.sh" >&2; exit 1; }
sudo -n true 2>/dev/null || { echo "sudo must work without a password for $(id -un)" >&2; exit 1; }

# --- Homebrew: the SDK's bootstrap sdkmanager, and the Java it runs on ---------------------------
[ -x /opt/homebrew/share/android-commandlinetools/cmdline-tools/latest/bin/sdkmanager ] ||
  brew install --cask android-commandlinetools
[ -d /opt/homebrew/opt/openjdk@21 ] || brew install openjdk@21

# --- The Android SDK and the AVD -------------------------------------------------------------------
export JAVA_HOME=/opt/homebrew/opt/openjdk@21
export ANDROID_SDK_ROOT="$HOME/Library/Android/sdk"
export ANDROID_HOME="$ANDROID_SDK_ROOT"
bootstrap=/opt/homebrew/share/android-commandlinetools/cmdline-tools/latest/bin/sdkmanager
mkdir -p "$ANDROID_SDK_ROOT"
yes | "$bootstrap" --sdk_root="$ANDROID_SDK_ROOT" --licenses >/dev/null 2>&1 || true
# cmdline-tools goes INTO this SDK as well: avdmanager finds the SDK from its own location,
# ignoring ANDROID_SDK_ROOT, so ours must live in this SDK to see this SDK's system image.
# Only what's missing is installed: asking sdkmanager for a package it has updates it, and other
# emulators on the Mac may run from this SDK too.
missing=()
[ -x "$ANDROID_SDK_ROOT/cmdline-tools/latest/bin/avdmanager" ] || missing+=("cmdline-tools;latest")
[ -x "$ANDROID_SDK_ROOT/platform-tools/adb" ] || missing+=("platform-tools")
[ -x "$ANDROID_SDK_ROOT/emulator/emulator" ] || missing+=("emulator")
[ -d "$ANDROID_SDK_ROOT/$(echo "$FT_IMAGE" | tr ';' '/')" ] || missing+=("$FT_IMAGE")
if [ ${#missing[@]} -gt 0 ]; then
  "$bootstrap" --sdk_root="$ANDROID_SDK_ROOT" "${missing[@]}"
fi
avdmanager="$ANDROID_SDK_ROOT/cmdline-tools/latest/bin/avdmanager"
if ! "$avdmanager" list avd 2>/dev/null | grep -q "Name: $FT_AVD\$"; then
  echo no | "$avdmanager" create avd -n "$FT_AVD" -k "$FT_IMAGE" --force
fi

# --- The emulator the lab runs: its own pinned copy -------------------------------------------------
# The Mac's other emulators run from the SDK's emulator, so the lab never updates that one (it would
# change them too). It runs this copy instead, which takes only the system image from the SDK.
emu="$HOME/Library/Android/ft-emulator/$FT_EMU_VERSION"
if [ ! -x "$emu/emulator/emulator" ]; then
  tmp=$(mktemp -d)
  curl -fsSL -o "$tmp/emulator.zip" "https://dl.google.com/android/repository/$FT_EMU_ZIP"
  if ! echo "$FT_EMU_SHA256  $tmp/emulator.zip" | shasum -a 256 -c - >/dev/null; then
    echo "$FT_EMU_ZIP doesn't match its sha256 in settings.yml" >&2
    rm -rf "$tmp"
    exit 1
  fi
  unzip -q "$tmp/emulator.zip" -d "$tmp/unzipped"
  mkdir -p "$(dirname "$emu")"
  rm -rf "$emu"
  mv "$tmp/unzipped" "$emu"
  rm -rf "$tmp"
fi

# --- A Bionic OpenSSL for ft's crypto backends -------------------------------------------------
# Android ships BoringSSL, which lacks OpenSSL symbols .NET binds ("a2d_ASN1_OBJECT"). The Android
# rows push these into the emulator and run ft with LD_LIBRARY_PATH pointing at them. From Termux's
# openssl package (built for Bionic). The UNVERSIONED names matter: .NET looks for libssl.so.
ossl="$HOME/Library/Android/ft-openssl"
ossl_version="1%3A3.6.3"
if [ ! -f "$ossl/libssl.so" ] || [ ! -f "$ossl/libcrypto.so" ]; then
  mkdir -p "$ossl"
  tmp=$(mktemp -d)
  ( cd "$tmp"
    curl -fsSL -o o.deb "https://packages.termux.dev/apt/termux-main/pool/main/o/openssl/openssl_${ossl_version}_aarch64.deb"
    tar xf o.deb
    for f in data.tar.xz data.tar.zst data.tar.gz data.tar; do [ -f "$f" ] && tar xf "$f" && break; done
    lib="data/data/com.termux/files/usr/lib"
    cp -f "$lib/libcrypto.so.3" "$ossl/libcrypto.so"
    cp -f "$lib/libssl.so.3" "$ossl/libssl.so" )
  rm -rf "$tmp"
fi

# --- Termux's sshfs, so an emulator can be an sshfs client, as a Termux user's `pkg install sshfs` --
# sshfs, openssh and their whole dependency closure from Termux's main AND root repositories (sshfs
# and fuse live in root), unpacked at Termux's prefix. The sshfs rows push it to the same prefix in
# the emulator (/data/data/com.termux/files/usr), so the binaries' built-in paths just work.
sshfs_dir="$HOME/Library/Android/ft-sshfs"
if [ ! -x "$sshfs_dir/usr/bin/sshfs" ]; then
  mkdir -p "$sshfs_dir"
  python3 "$(dirname "$0")/stage-termux-sshfs.py" "$sshfs_dir"
fi

# --- The Mac's SMB share: ~/ftshare as `ftshare`, for the account FT_SMB_USER ---------------------
sudo launchctl enable system/com.apple.smbd
sudo launchctl bootstrap system /System/Library/LaunchDaemons/com.apple.smbd.plist 2>/dev/null || true
mkdir -p "$HOME/ftshare"
chmod 777 "$HOME/ftshare"
sudo sharing -l | grep -q 'name:.*ftshare' || sudo sharing -a "$HOME/ftshare" -S ftshare -s 001 -g 000
id "$FT_SMB_USER" >/dev/null 2>&1 ||
  sudo sysadminctl -addUser "$FT_SMB_USER" -fullName 'File Tunnel SMB' -password "$FT_SMB_PASSWORD" >/dev/null 2>&1
# macOS keeps no SMB (NT) hash for a new account, so SMB logins fail until the hash list includes
# it and the password is set again. (dscl works for this account; sysadminctl would need a
# secure-token unlock.)
sudo dscl . -create "/Users/$FT_SMB_USER" AuthenticationAuthority ';ShadowHash;HASHLIST:<SALTED-SHA512-PBKDF2,SMB-NT>'
sudo dscl . -passwd "/Users/$FT_SMB_USER" "$FT_SMB_PASSWORD"

# --- Where the tests mount the other machines' shares, and the Mac's sshfs export ---------------
mkdir -p "$HOME/mnt/smb" "$HOME/mnt/nfs" "$HOME/ftsshfs"
chmod 777 "$HOME/ftsshfs"

echo "$(sw_vers -productName) $(sw_vers -productVersion): AVD $FT_AVD on emulator $FT_EMU_VERSION, OpenSSL and sshfs staged, SMB share ftshare for $FT_SMB_USER"

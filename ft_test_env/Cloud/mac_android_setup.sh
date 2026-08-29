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

# Stage the Termux sshfs toolchain (issue #45) so the emulator can be an sshfs CLIENT, exactly like a Termux
# user's `pkg install sshfs`. Resolves sshfs + openssh + their whole dependency closure across termux-main AND
# termux-root (sshfs/fuse live in root-repo), extracts the Termux prefix, and leaves it at
# ~/Library/Android/ft-sshfs/usr. AndroidProcessRunner pushes that to the device's Termux prefix
# (/data/data/com.termux/files/usr) and mounts .81:/srv/sshfs. Idempotent (the ~86MB download runs once).
SSHFS_DIR="$HOME/Library/Android/ft-sshfs"
if [ ! -x "$SSHFS_DIR/usr/bin/sshfs" ]; then
  echo "=== staging Termux sshfs toolchain (sshfs + openssh + deps) ==="
  mkdir -p "$SSHFS_DIR"
  python3 - "$SSHFS_DIR" <<'PYEOF'
import io, lzma, gzip, bz2, os, shutil, sys, tarfile, urllib.request
OUT = sys.argv[1]; ARCH = "aarch64"
# Each Termux repo has its own dist/component layout (from its apt sources line).
REPOS = [("https://packages.termux.dev/apt/termux-main", "dists/stable/main/binary-%s" % ARCH),
         ("https://packages.termux.dev/apt/termux-root", "dists/root/stable/binary-%s" % ARCH)]
SEEDS = ["sshfs", "openssh"]                 # openssh provides the `ssh` binary sshfs execs
PREFIX = "data/data/com.termux/files/usr"
def fetch(u):
    with urllib.request.urlopen(u, timeout=90) as r: return r.read()
def index(base, dist, pkgs):
    raw = None
    for nm in ("Packages.xz", "Packages.gz", "Packages"):
        try: raw = fetch("%s/%s/%s" % (base, dist, nm))
        except Exception: continue
        if nm.endswith(".xz"): raw = lzma.decompress(raw)
        elif nm.endswith(".gz"): raw = gzip.decompress(raw)
        break
    if raw is None: sys.exit("no Packages index from %s" % base)
    for blk in raw.decode("utf-8", "replace").split("\n\n"):
        d = {}; k = None
        for ln in blk.split("\n"):
            if ln[:1] in " \t" and k: d[k] += " " + ln.strip()
            elif ":" in ln:
                k, v = ln.split(":", 1); k = k.strip(); d[k] = v.strip()
        if "Package" in d and d["Package"] not in pkgs:   # first repo (main) wins
            d["_base"] = base; pkgs[d["Package"]] = d
def deps(s):
    out = []
    for g in (s or "").split(","):
        n = g.split("|")[0].split("(")[0].strip()
        if n: out.append(n)
    return out
def ar(blob):
    assert blob[:8] == b"!<arch>\n"; o = 8
    while o + 60 <= len(blob):
        h = blob[o:o+60]; o += 60
        nm = h[0:16].decode().strip().rstrip("/"); sz = int(h[48:58].decode().strip())
        yield nm, blob[o:o+sz]; o += sz
        if o % 2: o += 1
def extract(blob, dest):
    for nm, data in ar(blob):
        if nm.startswith("data.tar"):
            c = nm.rsplit(".", 1)[-1]
            raw = (lzma.decompress(data) if c == "xz" else gzip.decompress(data) if c == "gz"
                   else bz2.decompress(data) if c == "bz2" else data)
            with tarfile.open(fileobj=io.BytesIO(raw)) as tf:
                try: tf.extractall(dest, filter="tar")   # py>=3.12
                except TypeError: tf.extractall(dest)     # py<3.12 (trusted Termux source)
pkgs = {}
for b, dist in REPOS: index(b, dist, pkgs)
seen, order, stack = set(), [], list(SEEDS)
while stack:
    n = stack.pop(0)
    if n in seen or n not in pkgs: continue   # not in index => provided by the system (e.g. libc)
    seen.add(n); order.append(n); stack += deps(pkgs[n].get("Depends", ""))
build = os.path.join(OUT, "_build"); shutil.rmtree(build, ignore_errors=True); os.makedirs(build)
for n in order:
    extract(fetch("%s/%s" % (pkgs[n]["_base"], pkgs[n]["Filename"])), build)
dst = os.path.join(OUT, "usr"); shutil.rmtree(dst, ignore_errors=True)
shutil.move(os.path.join(build, PREFIX), dst); shutil.rmtree(build, ignore_errors=True)
assert os.path.exists(os.path.join(dst, "bin", "sshfs")), "sshfs missing after staging"
print("staged %d packages -> %s" % (len(order), dst))
PYEOF
fi
[ -x "$SSHFS_DIR/usr/bin/sshfs" ] && echo "sshfs toolchain ready: $SSHFS_DIR/usr" || echo "WARNING: sshfs toolchain not staged (Android sshfs rows will skip)"
echo SETUP_DONE

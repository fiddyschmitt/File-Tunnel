"""Stages Termux's sshfs (and openssh, which provides the `ssh` sshfs runs) with their whole
dependency closure, unpacked at Termux's prefix, under <out>/usr. setup.sh runs it on the Mac.

Each Termux repository has its own layout (from its apt sources line); sshfs and fuse are in the
root repository, the rest in main. Standard library only.

Usage: python3 stage-termux-sshfs.py <out>
"""
import bz2
import gzip
import io
import lzma
import os
import shutil
import sys
import tarfile
import urllib.request

OUT = sys.argv[1]
ARCH = "aarch64"
REPOS = [
    ("https://packages.termux.dev/apt/termux-main", "dists/stable/main/binary-%s" % ARCH),
    ("https://packages.termux.dev/apt/termux-root", "dists/root/stable/binary-%s" % ARCH),
]
SEEDS = ["sshfs", "openssh"]
PREFIX = "data/data/com.termux/files/usr"


def fetch(url):
    with urllib.request.urlopen(url, timeout=90) as response:
        return response.read()


def index(base, dist, packages):
    raw = None
    for name in ("Packages.xz", "Packages.gz", "Packages"):
        try:
            raw = fetch("%s/%s/%s" % (base, dist, name))
        except Exception:
            continue
        if name.endswith(".xz"):
            raw = lzma.decompress(raw)
        elif name.endswith(".gz"):
            raw = gzip.decompress(raw)
        break
    if raw is None:
        sys.exit("no Packages index from %s" % base)
    for block in raw.decode("utf-8", "replace").split("\n\n"):
        fields, key = {}, None
        for line in block.split("\n"):
            if line[:1] in " \t" and key:
                fields[key] += " " + line.strip()
            elif ":" in line:
                key, value = line.split(":", 1)
                key = key.strip()
                fields[key] = value.strip()
        if "Package" in fields and fields["Package"] not in packages:  # the first repo (main) wins
            fields["_base"] = base
            packages[fields["Package"]] = fields


def depends(field):
    names = []
    for group in (field or "").split(","):
        name = group.split("|")[0].split("(")[0].strip()
        if name:
            names.append(name)
    return names


def ar_members(blob):
    assert blob[:8] == b"!<arch>\n"
    offset = 8
    while offset + 60 <= len(blob):
        header = blob[offset:offset + 60]
        offset += 60
        name = header[0:16].decode().strip().rstrip("/")
        size = int(header[48:58].decode().strip())
        yield name, blob[offset:offset + size]
        offset += size
        if offset % 2:
            offset += 1


def extract(deb, dest):
    for name, data in ar_members(deb):
        if name.startswith("data.tar"):
            compression = name.rsplit(".", 1)[-1]
            raw = (lzma.decompress(data) if compression == "xz"
                   else gzip.decompress(data) if compression == "gz"
                   else bz2.decompress(data) if compression == "bz2"
                   else data)
            with tarfile.open(fileobj=io.BytesIO(raw)) as tar:
                try:
                    tar.extractall(dest, filter="tar")  # Python 3.12 and later
                except TypeError:
                    tar.extractall(dest)                # older Python (a trusted source)


packages = {}
for base, dist in REPOS:
    index(base, dist, packages)

seen, order, stack = set(), [], list(SEEDS)
while stack:
    name = stack.pop(0)
    if name in seen or name not in packages:  # not in the index: provided by the system (libc...)
        continue
    seen.add(name)
    order.append(name)
    stack += depends(packages[name].get("Depends", ""))

build = os.path.join(OUT, "_build")
shutil.rmtree(build, ignore_errors=True)
os.makedirs(build)
for name in order:
    extract(fetch("%s/%s" % (packages[name]["_base"], packages[name]["Filename"])), build)
destination = os.path.join(OUT, "usr")
shutil.rmtree(destination, ignore_errors=True)
shutil.move(os.path.join(build, PREFIX), destination)
shutil.rmtree(build, ignore_errors=True)
assert os.path.exists(os.path.join(destination, "bin", "sshfs")), "sshfs missing after staging"
print("staged %d Termux packages in %s" % (len(order), destination))

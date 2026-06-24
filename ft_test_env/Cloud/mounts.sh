#!/bin/bash
# Idempotent, non-fatal cross-host mounts for File Tunnel test nodes.
#
# Single source of truth for the network / shared-folder mounts. Used two ways:
#   1. cloud-init runs it during provisioning (written to /opt/ft/mounts.sh by the seed).
#   2. The orchestrator's "Re-mount shares" option streams and runs it over SSH (no reboot),
#      e.g. after bringing a previously-offline Windows share host online.
#
# Each share is mounted only if not already mounted, each mount is bounded by a timeout so an
# offline host can't hang the run, and a failure is logged but never aborts the rest.

MOUNT_TIMEOUT=25

remount() {
    local type="$1" src="$2" mp="$3" opts="$4"
    mkdir -p "$mp"
    if mountpoint -q "$mp"; then
        echo "Already mounted: $mp"
        return 0
    fi
    if [ -n "$opts" ]; then
        timeout "$MOUNT_TIMEOUT" mount -t "$type" "$src" "$mp" -o "$opts" \
            && echo "Mounted: $src -> $mp" \
            || echo "WARNING: mount of $src ($type) failed"
    else
        timeout "$MOUNT_TIMEOUT" mount -t "$type" "$src" "$mp" \
            && echo "Mounted: $src -> $mp" \
            || echo "WARNING: mount of $src ($type) failed"
    fi
}

# vboxsf needs the guest modules loaded first
modprobe vboxguest 2>/dev/null || true
modprobe vboxsf   2>/dev/null || true

remount cifs   "//192.168.0.31/e"        "/media/smb/192.168.0.31/e"          "username=Smith,password=villa2001"
remount cifs   "//192.168.0.31/r"        "/media/smb/192.168.0.31/r"          "username=Smith,password=villa2001"
remount cifs   "//192.168.0.81/data"     "/media/smb/192.168.0.81/data"       "password="
remount nfs    "192.168.0.81:/mnt/tmpfs" "/media/nfs/192.168.0.81/tmpfs"      ""
remount cifs   "//192.168.0.32/Shared"   "/media/smb/192.168.0.32/shared"     "username=Smith,password=villa2001"
remount vboxsf "C_DRIVE"                 "/media/vboxsf/192.168.0.31/c_drive" ""
remount 9p     "192.168.0.81"           "/media/9p/192.168.0.81/export"      "trans=tcp,aname=/srv/9p"

# Always succeed: per-mount status is reported above and verified by the orchestrator's checks.
exit 0

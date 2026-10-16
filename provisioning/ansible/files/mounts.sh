#!/bin/bash
# Mounts the File Tunnel lab's shares that aren't mounted, or are mounted but dead: every cifs, nfs
# and 9p entry in /etc/fstab (the playbook puts them there as `noauto`). Run as root at boot by
# ft-mounts.service, at the end of provisioning, and by the tests before a run. Idempotent, and it
# never fails: a server that is down only gets a warning, and the next run picks it up.
#
# A mount can be listed yet unusable: a 9p mount whose diod restarted (the kernel's 9p client can't
# reconnect), or a CIFS or NFS mount whose server went away or dropped an idle session. stat() on
# such a mount point fails, or a write hangs. Mounting again on top of it would stack a fresh mount
# over the dead one, and the dead one is what path lookups land on, so it would stay broken however
# often this ran. So health means "a write as root works" (ft runs as root), and anything dead is
# unmounted - as often as it is stacked - before mounting again.
#
# FT_MOUNT_RETRIES (default 1) is how many times to try each mount: at boot (ft-mounts.service) a
# server on this same machine may not quite be ready yet, so the boot run tries a few times.
set -u
exec 9>/run/ft-mounts.lock
flock 9                          # one run at a time (boot, provisioning and the tests can overlap)

MOUNT_TIMEOUT=25
RETRIES=${FT_MOUNT_RETRIES:-1}

healthy() {
    local mp="$1" probe
    timeout -s KILL 8 mountpoint -q "$mp" 2>/dev/null || return 1
    probe="$mp/.ft-mount-probe.$$"
    # Create and remove a file (not `touch`, which also sets times: that alone can be refused).
    # SIGKILL: a process stuck on a dead hard NFS mount ignores SIGTERM.
    timeout -s KILL 8 sh -c ': > "$1"' sh "$probe" 2>/dev/null || return 1
    timeout -s KILL 8 rm -f "$probe" 2>/dev/null
    return 0
}

mount_with_retries() {
    local mp="$1" attempt
    for attempt in $(seq 1 "$RETRIES"); do
        timeout -s KILL "$MOUNT_TIMEOUT" mount "$mp" </dev/null 2>&1 && return 0
        [ "$attempt" -lt "$RETRIES" ] && sleep 5
    done
    return 1
}

unmount_all_layers() {
    local mp="$1" i
    for i in 1 2 3 4 5; do
        grep -q " $mp " /proc/mounts 2>/dev/null || return 0
        timeout -s KILL "$MOUNT_TIMEOUT" umount -l "$mp" 2>/dev/null || return 0
    done
}

# Read the list first: nothing in the loop then reads its input from the list.
mapfile -t mount_points < <(findmnt --fstab --noheadings --output TARGET --types cifs,nfs,nfs4,9p)

for mp in "${mount_points[@]}"; do
    if healthy "$mp"; then
        echo "Already mounted: $mp"
        continue
    fi
    if grep -q " $mp " /proc/mounts 2>/dev/null; then
        echo "Unmounting a dead mount: $mp"
        unmount_all_layers "$mp"
    fi
    mkdir -p "$mp"
    if mount_with_retries "$mp"; then
        echo "Mounted: $mp"
    else
        echo "WARNING: couldn't mount $mp (is its server up?)"
    fi
done
exit 0

#!/bin/bash
set -e

# NOTE: networking (static IP, default gateway, DNS) is configured by cloud-init's
# network-config on first boot. The original manual "route add default gw" and
# "echo nameserver > /etc/resolv.conf" lines have been removed.




# Update package lists and install NFS server
apt-get update -y
apt-get install -y nfs-kernel-server

# Create tmpfs mount point and mount it
mkdir -p /mnt/tmpfs
mount -o size=3G -t tmpfs none /mnt/tmpfs

# Add export entry for NFS
echo "/mnt/tmpfs *(rw,sync,no_root_squash,no_subtree_check)" | tee -a /etc/exports

# Apply export changes and restart NFS service
exportfs -a
systemctl restart nfs-kernel-server



# Install Samba packages
apt-get install -y samba samba-client

# Configure Samba share
mkdir -p /media/smbserver/data
chmod -R 777 /media/smbserver/data

bash -c 'cat >> /etc/samba/smb.conf <<EOF

[data]
   read only = no
   writable = yes
   path = /media/smbserver/data
   guest ok = yes
EOF'

# Restart Samba service
service smbd restart



##### Setup an SSHFS share (Linux-only file share tunnelled over SSH)
# sshfs needs no daemon of its own — it rides the existing sshd. We only install the client package
# (every Debian node gets it, so any node can act as an sshfs client) and create a world-writable
# export directory that the SSH login user can read/write. Clients mount this over sshfs at test
# time (see ft_tests SshfsClient). Kept on disk (not tmpfs) so it survives reboots.
apt-get install -y sshfs
mkdir -p /srv/sshfs
chmod 777 /srv/sshfs



##### Setup a 9P share (Plan 9 filesystem protocol, served by diod over TCP - Linux-only)
# diod is a userspace 9P2000.L server; the client side is the in-kernel 9p driver (no package), so
# diod is installed only on the server. Export a world-writable dir over TCP :564 with munge auth
# disabled so the kernel client can mount with plain trans=tcp. The Debian package ships a SysV
# init script gated by DIOD_ENABLE in /etc/default/diod, so flip that on. On disk (not tmpfs) so it
# survives reboots. Clients mount this at test time (see ft_tests NinePClient).
apt-get install -y diod
mkdir -p /srv/9p
chmod 777 /srv/9p
cat > /etc/diod.conf <<'EOF'
exports = { "/srv/9p" }
auth_required = 0
EOF
sed -i 's/^DIOD_ENABLE=.*/DIOD_ENABLE=true/' /etc/default/diod
systemctl enable diod
systemctl restart diod



# Mount cross-host shares. Single source of truth is mounts.sh (written to /opt/ft/mounts.sh by
# the cloud-init seed); it is idempotent and non-fatal, and the orchestrator can re-run it over
# SSH on demand. Keeping the mounts there avoids duplicating them between provisioning and remount.
bash /opt/ft/mounts.sh


##### Setup an FTP server

FTP_DIR="/srv/ftp/pub"          # anonymous chroot (must NOT be writable)
UPLOAD_DIR="${FTP_DIR}/uploads"  # folder that anon can write to (and read from)
CONF_FILE="/etc/vsftpd.conf"

apt-get update -y
DEBIAN_FRONTEND=noninteractive apt-get install -y vsftpd

cp "$CONF_FILE" "${CONF_FILE}.bak.$(date +%s)" 2>/dev/null || true

cat > "$CONF_FILE" <<EOF
listen=YES
listen_ipv6=NO

# Anonymous access rooted at /srv/ftp/pub
anonymous_enable=YES
no_anon_password=YES
anon_root=${FTP_DIR}

# Allow reading & directory listing (downloads)
download_enable=YES
dirlist_enable=YES
anon_world_readable_only=YES

# Allow uploads/writes to a subfolder
write_enable=YES
anon_upload_enable=YES
anon_mkdir_write_enable=YES
anon_other_write_enable=YES

# Make newly uploaded files world-readable so they can be downloaded
anon_umask=022
file_open_mode=0644

# Passive mode for better client compatibility (adjust address/ports as needed)
pasv_enable=YES
# If behind NAT, set this to your public IP/DNS:
# pasv_address=0.0.0.0
pasv_min_port=40000
pasv_max_port=40100
EOF

# Create directory tree with safe ownership/permissions for chroot
install -d -m 755 -o root -g root /srv/ftp
install -d -m 755 -o root -g root "$FTP_DIR"
# uploads dir must NOT be owned by root; allow ftp to write, and readable for downloads
install -d -m 775 -o ftp  -g ftp  "$UPLOAD_DIR"

systemctl restart vsftpd
systemctl enable vsftpd

# (Optional) Fix permissions on any existing files so they're downloadable
chmod -R a+rX "$FTP_DIR"

echo "Anonymous FTP ready. Downloads are readable, uploads go in: ${UPLOAD_DIR}"



##### Setup an HTTP proxy on 0.0.0.0:8888

apt-get install -y tinyproxy

TINYPROXY_CONF="/etc/tinyproxy/tinyproxy.conf"
cp "$TINYPROXY_CONF" "${TINYPROXY_CONF}.bak.$(date +%s)" 2>/dev/null || true

sed -i 's/^Port .*/Port 8888/' "$TINYPROXY_CONF"
sed -i 's/^Listen .*/Listen 0.0.0.0/' "$TINYPROXY_CONF"
grep -q '^Listen ' "$TINYPROXY_CONF" || echo "Listen 0.0.0.0" >> "$TINYPROXY_CONF"
sed -i 's/^Allow /#Allow /' "$TINYPROXY_CONF"

systemctl restart tinyproxy
systemctl enable tinyproxy

echo "HTTP proxy (tinyproxy) listening on 0.0.0.0:8888"


# Mark the node ready as soon as the STANDARD services are up. The nested QEMU guest section below (with
# its multi-minute first-boot image build) deliberately runs AFTER this so it never delays node readiness;
# the ftq-guest service brings the guest up when its image is ready, and the virtio tests wait for it.
touch /run/ft-setup-complete



##### Setup a nested QEMU guest exposing virtio-fs + virtio-9p shares (only the node with a data disk)
# Only the QEMU-host node has a second disk (attached by VBoxManager, along with --nested-hw-virt). It
# runs a nested QEMU guest with nested KVM, exposing /srv/ftvfs to the guest via virtio-fs (virtiofsd)
# and /srv/ft9p via virtio-9p (QEMU -virtfs), and forwarding the guest's SSH to host:2222. The guest runs
# the *generic* kernel (the Debian cloud kernel ships no 9p module) plus libicu (for ft's .NET TimeZoneInfo),
# both baked into the guest image once by virt-customize. ft_tests' VirtioFs/Virtio9p tests then run ft
# host<->guest over each share. Wrapped in a non-fatal subshell so any failure never blocks the readiness
# sentinel below (and other nodes, with only one disk, skip this entirely).
#
# Identify the data disk as the whole disk that is NOT the root's disk. VirtualBox enumeration is not
# stable (here the immutable base is sdb and the 15G data disk is sda), so never hard-code sda/sdb.
FT_ROOTDISK=$(lsblk -no PKNAME "$(findmnt -no SOURCE / 2>/dev/null)" 2>/dev/null | head -1)
FT_DATADISK=""
for d in $(lsblk -dn -o NAME 2>/dev/null | grep -E '^(sd|vd|nvme)'); do
  [ "$d" = "$FT_ROOTDISK" ] && continue
  FT_DATADISK="/dev/$d"; break
done

if [ -n "$FT_DATADISK" ]; then
  ( set +e
    echo "=== Provisioning nested QEMU guest (data disk: $FT_DATADISK) ==="

    # Persist QEMU images on the data disk - the immutable root is tiny and resets on power-cycle. Format
    # only when blank (first boot); later boots already hold the built guest image, so keep it.
    blkid "$FT_DATADISK" >/dev/null 2>&1 || mkfs.ext4 -F -q "$FT_DATADISK"
    mkdir -p /var/lib/ftq
    grep -q '/var/lib/ftq' /etc/fstab || echo "$FT_DATADISK /var/lib/ftq ext4 defaults,nofail 0 2" >> /etc/fstab
    mountpoint -q /var/lib/ftq || mount "$FT_DATADISK" /var/lib/ftq
    chmod 777 /var/lib/ftq
    mkdir -p /srv/ftvfs /srv/ft9p && chmod 777 /srv/ftvfs /srv/ft9p

    # libguestfs-tools is large; redirect apt's download cache to the data disk to spare the ~2.8GB root.
    rm -rf /var/cache/apt/archives 2>/dev/null
    mkdir -p /var/lib/ftq/apt-cache/partial
    ln -sfn /var/lib/ftq/apt-cache /var/cache/apt/archives

    DEBIAN_FRONTEND=noninteractive apt-get install -y qemu-system-x86 qemu-utils virtiofsd cloud-image-utils libguestfs-tools

    # guest cloud-init seed (minimal SSH user; the tests mount the shares themselves)
    cat > /var/lib/ftq/user-data <<'CIEOF'
#cloud-config
ssh_pwauth: true
users:
  - name: user
    plain_text_passwd: live
    lock_passwd: false
    sudo: 'ALL=(ALL) NOPASSWD:ALL'
    shell: /bin/bash
chpasswd:
  expire: false
CIEOF
    printf 'instance-id: ftguest-001\nlocal-hostname: ftguest\n' > /var/lib/ftq/meta-data

    # offline image-customise: drop the cloud kernel so the 9p-capable generic kernel becomes default
    cat > /var/lib/ftq/customize.sh <<'CUEOF'
for p in $(dpkg-query -W -f='${Package}\n' 2>/dev/null | grep -- '-cloud-amd64'); do apt-get purge -y "$p" || true; done
update-grub || true
CUEOF

    # guest launcher: disposable overlay; virtio-fs via virtiofsd, virtio-9p via -virtfs, SSH forwarded to :2222
    cat > /var/lib/ftq/start-guest.sh <<'SGEOF'
#!/bin/bash
FTQ=/var/lib/ftq; RUN=/run/ftq; GUESTMEM=1024
mkdir -p "$RUN" /srv/ftvfs /srv/ft9p; chmod 777 /srv/ftvfs /srv/ft9p
[ -f "$RUN/qemu.pid" ] && kill "$(cat "$RUN/qemu.pid")" 2>/dev/null
pkill -f 'virtiofsd.*ftq/vfs.sock' 2>/dev/null
sleep 1
rm -f "$FTQ/guest.qcow2"
qemu-img create -f qcow2 -b "$FTQ/guest-base.qcow2" -F qcow2 "$FTQ/guest.qcow2" >/dev/null || exit 1
cloud-localds "$FTQ/seed.iso" "$FTQ/user-data" "$FTQ/meta-data" || exit 1
/usr/libexec/virtiofsd --socket-path="$RUN/vfs.sock" --shared-dir=/srv/ftvfs --cache=auto --sandbox=none >"$RUN/virtiofsd.log" 2>&1 &
sleep 1
qemu-system-x86_64 -enable-kvm -m "$GUESTMEM" -smp 2 -display none -serial "file:$RUN/console.log" \
  -drive "file=$FTQ/guest.qcow2,if=virtio,format=qcow2" -drive "file=$FTQ/seed.iso,if=virtio,format=raw" \
  -netdev user,id=n0,hostfwd=tcp::2222-:22 -device virtio-net-pci,netdev=n0 \
  -virtfs local,path=/srv/ft9p,mount_tag=ft9p,security_model=none \
  -chardev socket,id=cvfs,path="$RUN/vfs.sock" -device vhost-user-fs-pci,chardev=cvfs,tag=ftvfs \
  -object memory-backend-memfd,id=mem,size="${GUESTMEM}M",share=on -numa node,memdev=mem \
  -pidfile "$RUN/qemu.pid" -daemonize
SGEOF
    chmod +x /var/lib/ftq/start-guest.sh

    # Build the 9p+libicu guest image ONCE (persists on the data disk). It takes several minutes, so run
    # it DETACHED in the background - it must not delay the readiness sentinel. Write to a temp name and
    # atomically rename on success, so the ftq-guest service (which retries) only ever launches from a
    # complete image. virt-customize's appliance is a nested KVM VM; its temp goes on the data disk (the
    # immutable root is too small).
    if [ ! -f /var/lib/ftq/guest-base.qcow2 ] && [ ! -f /var/lib/ftq/guest-base.building.qcow2 ]; then
      apt-get clean
      mkdir -p /var/lib/ftq/vtmp
      setsid bash -c '
        cd /var/lib/ftq || exit 1
        wget -q https://cloud.debian.org/images/cloud/trixie/latest/debian-13-genericcloud-amd64.qcow2 -O guest-base.building.qcow2 \
          && env TMPDIR=/var/lib/ftq/vtmp LIBGUESTFS_CACHEDIR=/var/lib/ftq/vtmp LIBGUESTFS_BACKEND=direct \
               virt-customize -a guest-base.building.qcow2 \
                 --install linux-image-amd64,libicu-dev,tzdata --run /var/lib/ftq/customize.sh \
          && mv -f guest-base.building.qcow2 guest-base.qcow2 \
          || rm -f guest-base.building.qcow2
      ' >/var/lib/ftq/image-build.log 2>&1 </dev/null &
    fi

    # systemd unit to launch the nested guest at boot
    cat > /etc/systemd/system/ftq-guest.service <<'SVEOF'
[Unit]
Description=Nested QEMU guest exposing virtio-fs + virtio-9p shares for ft tests
After=var-lib-ftq.mount

[Service]
Type=forking
PIDFile=/run/ftq/qemu.pid
ExecStart=/bin/bash /var/lib/ftq/start-guest.sh
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
SVEOF
    systemctl daemon-reload
    systemctl enable ftq-guest
    systemctl start ftq-guest || echo "WARNING: ftq-guest failed to start"
    echo "=== nested QEMU guest provisioning done ==="
  ) || echo "WARNING: nested QEMU guest provisioning encountered an error (non-fatal)"
fi



# Signal that provisioning finished. The orchestrator polls for this file to decide a node is
# ready (more reliable than SSH or auto-started services, which come up before this script ends).
touch /run/ft-setup-complete
echo "Provisioning complete."

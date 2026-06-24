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


# Signal that provisioning finished. The orchestrator polls for this file to decide a node is
# ready (more reliable than SSH or auto-started services, which come up before this script ends).
touch /run/ft-setup-complete
echo "Provisioning complete."

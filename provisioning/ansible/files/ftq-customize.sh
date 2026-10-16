# Run inside the guest's image by virt-customize: drop the cloud kernel, which has no 9p module,
# so the generic kernel installed alongside it is the one that boots.
for p in $(dpkg-query -W -f='${Package}\n' 2>/dev/null | grep -- '-cloud-amd64'); do apt-get purge -y "$p" || true; done
update-grub || true

# File Tunnel test lab: the test machines, defined as code

The machines the end-to-end tests in `ft_tests` run ft on. A tunnel runs between two clients that
share a file server, so most of the lab comes in threes: client1, server, client2.

| Machine | Role | OS | Address | Runs on | Configured by |
|---|---|---|---|---|---|
| `ft-debian-1` | Linux client1, and the RdpLinux row's RDP client | Debian 13 | 192.168.0.80 | VirtualBox, this PC | `ansible/playbook.yml` |
| `ft-debian-2` | Linux server: NFS, SMB, 9P, sshfs, FTP, WebDAV, S3 | Debian 13 | 192.168.0.81 | VirtualBox, this PC | `ansible/playbook.yml` |
| `ft-debian-3` | Linux client2, and host of a nested QEMU guest (virtio-fs, virtio-9p) | Debian 13 | 192.168.0.82 (guest: port 2222) | VirtualBox, this PC | `ansible/playbook.yml` |
| `ft-win11-1` | Windows client1 | Windows 11 25H2 Enterprise (evaluation) | 192.168.0.83 | VirtualBox, this PC | `windows/provision.ps1` |
| `ft-win11-2` | Windows server: `\\<ip>\Shared`, the RDP target | Windows 11 25H2 Enterprise (evaluation) | 192.168.0.84 | VirtualBox, this PC | `windows/provision.ps1` |
| `ft-win11-3` | Windows client2 | Windows 11 25H2 Enterprise (evaluation) | 192.168.0.88 | VirtualBox, this PC | `windows/provision.ps1` |
| `ft-debian-build` | builds ft for Android; not in the tests | Debian 13 | 192.168.0.79 | VirtualBox, this PC (on demand) | `ansible/playbook.yml` |
| `mac-mini-m4` | the Mac: a test machine, and the emulators' host | macOS 26 | 192.168.0.33 | itself | `mac/setup.sh` |
| `emulator-5556`, `emulator-5558` | Android client1, client2 | Android 16, arm64 | the router's (bridged) | the Mac | `mac/emulators.sh` |
| this PC | runs the tests; shares its `C:\` and ft's log folder with the VMs | Windows 11 | 192.168.0.31 | | |

**Vagrant** builds and runs the VMs. **Ansible** (Linux), a PowerShell script (Windows) and shell
scripts (the Mac) configure what's inside them. You edit one file, `settings.yml`, and can rebuild any
machine identically at any time. The tests read the machines, their addresses and logins from that
same file (`ft_tests\Lab.cs`).

## Quick start

    cd C:\Users\Smith\Desktop\dev\cs\file_tunnel\provisioning
    vagrant up             # the six test VMs
    .\mac.ps1 setup        # once: the Mac's Android SDK and emulator image, and its SMB share
    .\mac.ps1 up           # the two Android emulators
    .\check.ps1            # is it all ready?

then publish ft to `R:\Temp\ft release\<rid>` (the "r drive" publish profiles; `.\build-android.ps1`
for Android), and from the repo:

    dotnet test ft_tests

The first `vagrant up` takes one to two hours: each Windows VM's first boot takes 15 to 20 minutes
(the box is 14 GB, already here if the rclone lab is), each Linux VM about 10 (most of it bringing
Debian up to date), and the nested QEMU guest's image a few more. After that, `vagrant up` just boots
them. The whole lab takes about 19 GB of memory, plus 6 GB while the build VM runs.

## Everyday commands

| Command | What it does |
|---|---|
| `vagrant up [name]` | Create/boot the VMs (provisions only when first created) |
| `vagrant halt` and `.\mac.ps1 halt` | Stop the lab |
| `vagrant reload <name>` | Restart a VM: clears a Windows VM that has tired over a long run |
| `vagrant provision [name]` | Re-apply the configuration to a running VM. Safe to repeat |
| `vagrant reload --provision [name]` | Restart with changed `memory_mb`/`cpus`/`ip`, then provision |
| `vagrant destroy -f <name>` then `vagrant up <name>` | Rebuild a VM from scratch |
| `vagrant ssh <name>` | Shell into a VM |
| `.\mac.ps1 up` | Start the emulators, or refresh them after the Mac or this PC restarted |
| `.\mac.ps1 status` / `setup` / `ssh` | The emulators' state / the Mac's setup / a shell on the Mac |
| `.\check.ps1` | Every machine's smoke test, over the LAN, as the tests reach them |
| `.\build-android.ps1` | Build ft for Android from this working tree, on the build VM |
| `vagrant ssh <name> -c "sudo /opt/ft/mounts.sh"` | Mount the lab's shares on a Linux VM now |
| `linux.kernel` in `settings.yml`, then `vagrant provision` | Boot the Linux VMs on another kernel (A/B tests); `newest` goes back |
| `ssh -i ~/.ssh/ft_lab_ed25519 user@192.168.0.80` | Log in as the tests do (`vagrant@` for Windows, `smith@` for the Mac) |

After every `vagrant up`, `reload` and `provision`, each VM runs its smoke test and shows its
PASS/WARN/FAIL lines; a FAIL stops Vagrant there.

## Files

| File | Role |
|---|---|
| `settings.yml` | The only file you normally edit: the machines, their roles, addresses and sizes, the logins, pinned downloads |
| `Vagrantfile` | The VirtualBox VMs: box, CPU/RAM, bridged network, shared folders, provisioners, smoke tests |
| `ansible/playbook.yml` | The configuration inside each Linux VM; what a VM gets depends on its role |
| `ansible/tasks/` | `base.yml` (every VM), `file_servers.yml`, `mounts.yml`, `rdp_client.yml`, `qemu_host.yml`, `build_host.yml` |
| `ansible/files/mounts.sh` | Mounts the lab's shares on a Linux VM, at boot and before every test run |
| `ansible/templates/smoke-test.sh.j2` | A Linux VM's smoke test (`/opt/ft/smoke-test.sh`) |
| `windows/provision.ps1` | The configuration inside each Windows VM |
| `windows/smoke-test.ps1` | A Windows VM's smoke test (`C:\ft-lab\smoke-test.ps1`) |
| `mac.ps1` | Runs the Mac's side from this PC, over SSH |
| `mac/setup.sh`, `mac/stage-termux-sshfs.py` | The Mac's setup, which `.\mac.ps1 setup` runs there |
| `mac/emulators.sh` | Starts, refreshes and stops the emulators, run there by `.\mac.ps1 up / halt / status` |
| `check.ps1` | Is the lab ready? |
| `build-android.ps1` | Builds ft for Android on the build VM |

## Design notes (the "why")

- **`ansible_local`**: Ansible can't run as a control node on Windows, so Vagrant installs it inside
  each Linux VM and runs the playbook there. No host tooling needed.
- **Idempotence**: each step declares a *state* ("package present", "share exported"), not an
  action, so `vagrant provision` changes nothing unless something drifted. Run it whenever you like.
- **Persistent machines**: the VMs keep their disks, the nested QEMU guest's image and the build VM's
  toolchains included. A clean slate is a deliberate `vagrant destroy -f <name>` and `vagrant up <name>`.
- **Debian kept current, the kernel included**: the kernel's SMB, NFS and 9P clients are among the
  clients ft is tested over, so the play keeps each Linux VM up to date, as a user's would be, and the
  Vagrantfile boots the chosen kernel. `linux.kernel` in `settings.yml` pins another one for an A/B
  test: the play fetches it from snapshot.debian.org by its file hash (Debian keeps only its current
  one) and makes it GRUB's default.
- **Fixed addresses on a bridged NIC**: the tests, the Mac and the emulators reach every VM at its LAN
  address, and the VMs reach each other the same way. Each VM's second NIC is bridged onto the LAN with
  a fixed address; NIC 1 stays NAT, for Vagrant's own connection and the VM's internet access. Keep the
  addresses out of the router's DHCP pool.
- **DNS through this PC's resolver**: each VM resolves names through VirtualBox's DNS relay on its NAT
  network, 10.0.2.3, which asks this PC's resolver at the time of each query. VirtualBox's DHCP would
  otherwise give a VM the DNS servers this PC had when the VM started, which go stale when they
  change, for example when a VPN on this PC connects or disconnects. The SOCKS rows' internet checks
  and the Dropbox rows need it.
- **Servers first**: `vagrant up` brings the VMs up in `settings.yml`'s order, servers first, so each
  Linux VM can mount the shares as it is provisioned.
- **The shares, mounted by a script, not at boot**: each Linux test VM mounts the Linux server's SMB,
  NFS and 9P shares and the Windows server's SMB share at `/media/<type>/<server address>/<share>`.
  They are in `/etc/fstab` as `noauto`, with each protocol's typical options and nothing tuned;
  `/opt/ft/mounts.sh` mounts whichever aren't, at boot (in the background, so a server that's down never
  holds up a boot) and when the tests start. "Mounted" means "a write as root works" (ft runs as root):
  an idle SMB session or a restarted server can leave a mount that is listed yet dead, and a dead one is
  unmounted, however many times it's stacked, before mounting again.
- **Every Linux test VM runs every server**: any could stand in for another. The tests use
  `ft-debian-2`'s.
- **This PC's shared folders**: `C:\` is shared into every VM as `c_drive` (`\\vboxsvr\c_drive`,
  `/media/vboxsf/<ip>/c_drive`): the VirtualBoxSharedFolder rows run ft over it. ft's output on the
  Linux VMs goes to `ft_logs`, this PC's `R:\Temp\ft release\linux-x64\output`, also a VirtualBox
  shared folder, so the VMs need no login to this PC.
- **One SSH key for the lab** (`ssh_key`, made on first use): every machine accepts it, and the tests
  log in to the Mac with it. It lives in `~/.ssh`, outside this folder, so it's never shared into a VM.
  The tests log in to the VMs with the lab's logins from `settings.yml` (SMB and RDP need passwords anyway).
- **Windows from a public box**: Microsoft publishes nothing like a cloud image for Windows, so the VMs
  start from gusztavvargadr/windows-11 (the rclone lab's too): a Packer-built, sysprepped Windows 11
  Enterprise evaluation with OpenSSH Server, WinRM, and automatic updates off. Vagrant talks WinRM to
  it; the tests use SSH. Being sysprepped, every VM made from it gets its own machine SID, which SMB and
  RDP logins between the VMs need: Windows 24H2 and later reject them between machines that share one.
- **The evaluation licence runs 90 days from a VM's creation.** Rebuild it to start again:
  `vagrant destroy -f ft-win11-1` and `vagrant up ft-win11-1`.
- **RunRemote**: the tests start ft on Windows through it (UDP 8888), inside the logged-on session,
  where `\\tsclient` (RDP drive redirection), the NFS drive and saved SMB credentials live; an SSH
  session can't see them. The account logs on at the console at every boot, RunRemote starts at logon
  and every two minutes after (a second copy exits while the first holds the port), so it comes back
  if it dies. It runs at normal priority, which ft inherits, as it would from a program you start
  yourself. It's installed from the PC's own build (`windows.runremote_dir`).
- **No Windows firewall**: the tests connect into ft's port forwards on whatever ports a test picks; a
  scoped rule proved unreliable here, and this is an isolated lab.
- **The S3 server is SILO**, the maintained fork of MinIO (whose community downloads are gone), pinned
  and checksum-verified. If its download fails, the rest of the VM is still provisioned, and the summary
  says so.
- **The Mac isn't a VM**, so there's no Vagrant there: `mac.ps1` copies `mac\` to it over SSH and runs
  it, so this folder stays the one place you edit. Its setup installs what Homebrew doesn't have yet.
- **The emulators**: two real ones, both bridged onto the LAN (so either can be either end of a
  tunnel), started one after the other (started together, only one gets Android's default network).
  After the Mac or this PC restarts, an emulator can hold its old address yet pass no traffic;
  `.\mac.ps1 up` checks that each reaches the LAN, and restarts its Wi-Fi, or the pair, if not. Only the
  two emulators on these ports are touched: other emulators on the Mac are left alone.
- **The lab's own emulator**: the Mac's other emulators run from the SDK in `~/Library/Android/sdk`,
  so the lab never updates anything there; it installs only what's missing. It runs its own copy of
  the emulator instead, pinned in `settings.yml` and kept in `~/Library/Android/ft-emulator/<version>`.
- **The Android build** runs on Linux because NativeAOT for linux-bionic-arm64 needs the Android NDK's
  Linux toolchain. It builds this PC's working tree (shared in read-only), uncommitted changes
  included, not what's on GitHub.
- **Pinned boxes and images**: every rebuild starts from the same bits. Bump on purpose.
- **Typical options everywhere**: the lab mounts and serves its shares the way a user would, on the
  kernel a user would have. When ft needs something special to be coherent over a share, that goes in
  ft, not in a mount option here.

## Troubleshooting

- A VM boots but isn't reachable at its address: the bridge adapter's name in `settings.yml` no longer
  matches the PC's active NIC (it changes after a reinstall, or when a cable is moved).
  `VBoxManage list bridgedifs` shows which is Up; fix `network.bridge_adapter`, then `vagrant reload`.
- A smoke test WARNs that a share isn't mounted: its server wasn't up. Once it is,
  `vagrant ssh <name> -c "sudo /opt/ft/mounts.sh"`, or just run the tests (they mount first).
- Rows on one Windows VM fail with "Could not connect" after a long run: `vagrant reload <name>`.
- A VM stops responding altogether (no ping, its CPUs busy): `VBoxManage controlvm <name> poweroff`,
  then `vagrant up <name>`.
- A Windows VM says its licence expired: rebuild it (see above).
- The Android rows are skipped, or fail with "Could not connect": `.\mac.ps1 up`.
- `ssh` warns that the host key changed: a rebuilt VM has new host keys. `ssh-keygen -R 192.168.0.80`
  forgets the old one. (The tests don't check host keys.)
- "`--inventory-file` argument is deprecated" while provisioning Linux: that's how Vagrant 2.4.9's
  `ansible_local` calls Ansible. Harmless.
- A VM won't start ("The VM session was closed before any attempt to power it on"): the PC is out of
  memory. The lab needs about 19 GB.

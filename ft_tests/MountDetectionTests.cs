using ft;

namespace ft_tests
{
    /// <summary>
    /// Hermetic tests for the statfs-magic mount classifier (<see cref="Extensions.ClassifyMountMagic"/>)
    /// - the pure core of ft's filesystem auto-detection, with no real mount or lab needed. Documents and
    /// locks in which filesystem magics map to which handling.
    ///
    /// Real-mount findings for the virtio transports (confirmed on QEMU, via the ft_test_env nested guest):
    ///   - virtio-fs and sshfs BOTH report the FUSE magic (0x65735546) -> Fuse here. ft separates them by the
    ///     mountinfo fstype ("virtiofs" vs "fuse.sshfs", not by statfs): virtio-fs runs Normal (its held handle
    ///     refreshes via fstat, ~2.4x faster), sshfs runs IsolatedIo. See Extensions.ModesForReadFile.
    ///   - virtio-9p (QEMU -virtfs) reports its BACKING filesystem's magic (e.g. ext4 0xEF53), NOT V9FS,
    ///     so it classifies as that fs -> Normal. A held handle there sees appends, so Normal works - unlike
    ///     diod's TCP-9p, which reports V9FS and needs upload-download. So the V9FS row below covers
    ///     9P-over-TCP (diod), not QEMU virtio-9p. BUT a held read handle on any Linux 9p mount goes stale
    ///     across a purge, so ReusableFile reopens it after each one; that is keyed off the mountinfo fstype
    ///     ("9p"), the only place virtio-9p is recognisable. See the IsNinePFsType test below.
    ///   - A WSL 2 drive (/mnt/c) ALSO reports V9FS, but is coherent and must run Normal: the Windows end of
    ///     the tunnel is on plain NTFS (Normal), and both ends have to be in the same mode. ft tells it apart
    ///     from network 9P by the mount's "aname=drvfs" option. See the ModesFor tests below.
    /// </summary>
    [TestClass]
    [TestCategory("Unit")]
    public class MountDetectionTests
    {
        [DataTestMethod]
        [DataRow(0x01021997, Extensions.MountKind.NineP, "V9FS - 9P-over-TCP (diod); QEMU virtio-9p reports its backing fs instead")]
        [DataRow(0x65735546, Extensions.MountKind.Fuse, "FUSE - sshfs AND virtio-fs (confirmed on a real QEMU virtio-fs mount)")]
        [DataRow(0x786F4256, Extensions.MountKind.Vboxsf, "VirtualBox shared folder (vboxsf)")]
        [DataRow(0x0000EF53, Extensions.MountKind.Other, "ext2/3/4 - coherent, no special handling")]
        [DataRow(0x00006969, Extensions.MountKind.Other, "NFS - coherent, no special handling")]
        [DataRow(0x00000000, Extensions.MountKind.Other, "statfs failed / unknown filesystem")]
        public void ClassifyMountMagic_MapsMagicToKind(int magic, Extensions.MountKind expected, string description)
        {
            Assert.AreEqual(expected, Extensions.ClassifyMountMagic(magic), description);
        }

        // /proc/self/mountinfo lines captured verbatim from real mounts: a WSL 2.7 drive (Ubuntu 24.04), and the
        // ft_test_env lab (ext4 root, diod 9P over TCP, sshfs, and QEMU virtio-9p / virtio-fs shares).
        private const string Root = "28 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw,discard,errors=remount-ro";
        private const string WslDrive = @"132 82 0:70 / /mnt/c rw,noatime - 9p C:\134 rw,aname=drvfs;path=C:\;uid=0;gid=0;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=6,wfd=6";
        private const string Diod = "99 28 0:63 / /mnt/9p rw,relatime shared:313 - 9p 192.168.0.81 rw,aname=/srv/9p,access=client,msize=65536,trans=tcp";
        private const string Sshfs = "241 28 0:57 / /home/user/share rw,nosuid,nodev,relatime shared:271 - fuse.sshfs user@192.168.0.81:/srv/sshfs rw,user_id=1000,group_id=1000";
        private const string Virtio9p = "179 28 0:53 / /mnt/v9p rw,relatime shared:229 - 9p ft9p rw,access=client,trans=virtio";
        private const string VirtioFs = "65 28 0:52 / /mnt/vfs rw,relatime shared:223 - virtiofs ftvfs rw";

        private static readonly string[] MountInfo = [Root, WslDrive, Diod, Sshfs, Virtio9p, VirtioFs];

        [DataTestMethod]
        [DataRow("/mnt/c/Temp", "9p", true, "a folder on a WSL drive")]
        [DataRow("/mnt/c", "9p", true, "the WSL drive's own mountpoint")]
        [DataRow("/mnt/9p/tunnel", "9p", false, "a network 9P share is not a WSL drive")]
        [DataRow("/home/user/share", "fuse.sshfs", false, "sshfs")]
        [DataRow("/mnt/v9p/tunnel", "9p", false, "QEMU virtio-9p is 9P too, but not a WSL drive")]
        [DataRow("/mnt/vfs/x/y", "virtiofs", false, "virtio-fs, nested folder")]
        [DataRow("/mnt/cache/data", "ext4", false, "/mnt/cache must not match the /mnt/c mount")]
        [DataRow("/srv/share", "ext4", false, "anything else falls back to the root mount")]
        public void FindMount_PicksTheLongestMountpointPrefix(string path, string expectedFsType, bool expectWslDrive, string description)
        {
            var (fsType, superOptions) = Extensions.FindMount(MountInfo, path);

            Assert.AreEqual(expectedFsType, fsType, description);
            Assert.AreEqual(expectWslDrive, Extensions.IsWslDriveOptions(superOptions), description);
        }

        // Which mounts get the read handle reopened after a purge (Extensions.IsNinePFileSystem): every 9p
        // mount, including QEMU virtio-9p, which statfs reports as its backing filesystem.
        [DataTestMethod]
        [DataRow("/mnt/v9p/tunnel", true, "QEMU virtio-9p")]
        [DataRow("/mnt/9p/tunnel", true, "9P over TCP (diod)")]
        [DataRow("/mnt/c/Temp", true, "a WSL drive")]
        [DataRow("/mnt/vfs/tunnel", false, "virtio-fs")]
        [DataRow("/home/user/share/tunnel", false, "sshfs")]
        [DataRow("/srv/share", false, "ext4")]
        public void IsNinePFsType_IsTrueForEveryNinePMount(string path, bool expected, string description)
        {
            Assert.AreEqual(expected, Extensions.IsNinePFsType(Extensions.FindMount(MountInfo, path).FsType), description);
        }

        [TestMethod]
        public void FindMount_ReturnsEmpty_WhenNothingMatchesOrALineIsMalformed()
        {
            Assert.AreEqual(("", ""), Extensions.FindMount([], "/mnt/c/Temp"));
            Assert.AreEqual(("", ""), Extensions.FindMount(["garbage without a separator", "1 2 3 - 9p"], "/mnt/c/Temp"));
        }

        // The mode ft picks when the user passes none, per kind of mount. The WSL row is the regression test
        // for v3.1.0-v3.5.4, which sent a WSL drive down the network-9P path (upload-download): the Windows end
        // stayed in Normal, so a tunnel started with no mode flags never came online.
        [DataTestMethod]
        [DataRow(Extensions.MountKind.NineP, WslDrive, Extensions.TunnelMode.Normal, "a WSL drive (9P)")]
        [DataRow(Extensions.MountKind.NineP, Diod, Extensions.TunnelMode.UploadDownload, "a 9P mount")]
        [DataRow(Extensions.MountKind.Fuse, Sshfs, Extensions.TunnelMode.IsolatedIo, "an sshfs / FUSE mount")]
        [DataRow(Extensions.MountKind.Fuse, VirtioFs, Extensions.TunnelMode.Normal, "a virtio-fs mount")]
        [DataRow(Extensions.MountKind.Vboxsf, Root, Extensions.TunnelMode.IsolatedIo, "a VirtualBox shared folder (vboxsf)")]
        [DataRow(Extensions.MountKind.Other, Root, Extensions.TunnelMode.Normal, "")]
        public void ModesFor_PrefersTheRightMode(Extensions.MountKind kind, string mountInfoLine, Extensions.TunnelMode expectedPreferred, string expectedDescription)
        {
            var mountpoint = mountInfoLine.Split(' ')[4];
            var (fsType, superOptions) = Extensions.FindMount([mountInfoLine], mountpoint);

            var modes = Extensions.ModesFor(kind, fsType, superOptions);

            Assert.AreEqual(expectedPreferred, modes.Preferred);
            Assert.AreEqual(expectedDescription, modes.Description);
            Assert.IsTrue(modes.Supports(expectedPreferred), "the preferred mode must be one of the known-good modes");
        }

        [TestMethod]
        public void ModesFor_WslDrive_SupportsEveryMode_ButNetworkNinePOnlyUploadDownload()
        {
            var wsl = Extensions.ModesFor(Extensions.MountKind.NineP, "9p", Extensions.FindMount([WslDrive], "/mnt/c").SuperOptions);
            var diod = Extensions.ModesFor(Extensions.MountKind.NineP, "9p", Extensions.FindMount([Diod], "/mnt/9p").SuperOptions);

            foreach (var mode in new[] { Extensions.TunnelMode.Normal, Extensions.TunnelMode.IsolatedIo, Extensions.TunnelMode.UploadDownload })
            {
                Assert.IsTrue(wsl.Supports(mode), $"WSL drive should support {mode}");
            }

            Assert.IsFalse(diod.Supports(Extensions.TunnelMode.Normal));
            Assert.IsFalse(diod.Supports(Extensions.TunnelMode.IsolatedIo));
            Assert.IsTrue(diod.Supports(Extensions.TunnelMode.UploadDownload));
        }
    }
}

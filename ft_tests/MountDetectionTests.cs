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
    ///     refreshes via fstat, ~2.4x faster), sshfs runs IsolatedReads. See Extensions.ModesForReadFile.
    ///   - virtio-9p (QEMU -virtfs) reports its BACKING filesystem's magic (e.g. ext4 0xEF53), NOT V9FS,
    ///     so it classifies as that fs -> Normal. That is correct: QEMU virtio-9p (cache=none) is coherent,
    ///     so Normal works - unlike diod's TCP-9p, which reports V9FS and IS incoherent (needs
    ///     upload-download). So the V9FS row below covers 9P-over-TCP (diod), not QEMU virtio-9p.
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
    }
}

using ft_tests.Runner;

namespace ft_tests.FileShares.Clients
{
    /// <summary>
    /// The guest-side ft endpoint of a virtio share test. Runs inside the nested QEMU guest on .82
    /// (reached over the host's SSH port-forward, .82:2222, via a <see cref="LinuxProcessRunner"/> with
    /// port 2222) where the host's exported directory appears as a virtio-fs or virtio-9p mount. Restart
    /// (re)mounts that share so the guest ft auto-detects it. The host side of the tunnel is a plain
    /// <see cref="Client"/> on the native export dir (no mount needed).
    /// </summary>
    public class VirtioGuestClient : Client
    {
        public const string VirtioFsMountPoint = "/mnt/vfs";
        public const string Virtio9pMountPoint = "/mnt/9p";

        private readonly ProcessRunner runner;
        private readonly string mountScript;

        private VirtioGuestClient(ProcessRunner runner, string args, string mountScript) : base(OS.Linux, runner, args)
        {
            this.runner = runner;
            this.mountScript = mountScript;
        }

        // virtio-fs: tag 'ftvfs' (matches the QEMU vhost-user-fs device); FUSE-based, auto-detected.
        public static VirtioGuestClient VirtioFs(ProcessRunner runner, string args) =>
            new(runner, args, $"mkdir -p {VirtioFsMountPoint}; mountpoint -q {VirtioFsMountPoint} || mount -t virtiofs ftvfs {VirtioFsMountPoint}");

        // virtio-9p: tag 'ft9p' (matches the QEMU -virtfs mount_tag); reports ext4 backing magic -> Normal.
        public static VirtioGuestClient Virtio9p(ProcessRunner runner, string args) =>
            new(runner, args, $"mkdir -p {Virtio9pMountPoint}; mountpoint -q {Virtio9pMountPoint} || mount -t 9p -o trans=virtio,version=9p2000.L ft9p {Virtio9pMountPoint}");

        public override void Restart()
        {
            runner.Run("bash", $"-c '{mountScript}'");
        }
    }
}

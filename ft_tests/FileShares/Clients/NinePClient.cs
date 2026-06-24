using ft_tests.FileShares.Servers;
using ft_tests.Runner;

namespace ft_tests.FileShares.Clients
{
    /// <summary>
    /// Mounts the <see cref="NinePServer"/>'s diod export over the in-kernel 9p client (Linux only),
    /// mirroring <see cref="NfsClient"/>/<see cref="SshfsClient"/>: tear down any stale mount, then
    /// mount -t 9p over TCP at a fixed mount point.
    ///
    /// Uses the most typical 9P-over-TCP mount with nothing special: <c>trans=tcp</c> (required for a
    /// network 9P transport) and <c>aname</c> (selects diod's export). Port (564), protocol version,
    /// uname, access and msize all take their defaults. Everything runs under sudo (see
    /// LinuxProcessRunner), so root owns the transferred files.
    /// </summary>
    public class NinePClient : Client
    {
        // Mirrors the NFS/sshfs /media/<type>/<server>/<share> convention.
        public const string MountPoint = "/media/9p/192.168.0.81/export";

        private readonly ProcessRunner runner;

        public NinePClient(OS os, ProcessRunner runner, string args) : base(os, runner, args)
        {
            this.runner = runner;
        }

        public override void Restart()
        {
            if (OS != OS.Linux) return; // 9p tests are Linux-only by design

            var opts = $"trans=tcp,aname={NinePServer.ExportDir}";

            var script =
                $"umount -l {MountPoint} 2>/dev/null || true; " +
                $"mkdir -p {MountPoint}; " +
                $"mount -t 9p -o {opts} {NinePServer.ServerIp} {MountPoint}";

            runner.Run("bash", $"-c '{script}'");
        }
    }
}

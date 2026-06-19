using ft_tests.FileShares.Servers;
using ft_tests.Runner;

namespace ft_tests.FileShares.Clients
{
    /// <summary>
    /// Mounts the <see cref="SshfsServer"/>'s export over sshfs (Linux clients only). The sshfs
    /// package itself is installed by provisioning (ft_test_env/Cloud/setup_debian.sh), not here —
    /// Restart only performs the runtime (re)mount, mirroring <see cref="NfsClient"/>:
    ///   1. tear down any stale/hung mount (fusermount -uz, then a lazy umount),
    ///   2. (re)mount user@.81:/srv/sshfs at a fixed local mount point.
    ///
    /// Auth: sshfs's reliable non-interactive method is "-o password_stdin" fed the SSH password on
    /// stdin (sshpass is unreliable with sshfs because it forks ssh without a tty). The whole thing
    /// runs under sudo (ft also runs as root), so root owns the FUSE mount and can read/write it
    /// without allow_other. StrictHostKeyChecking=no + UserKnownHostsFile=/dev/null avoid a
    /// first-connect host-key prompt that would otherwise hang the mount.
    /// </summary>
    public class SshfsClient : Client
    {
        // Mirrors the NFS client's /media/<type>/<server>/<share> convention.
        public const string MountPoint = "/media/sshfs/192.168.0.81/export";

        private const string SshUser = "user";
        private const string SshPassword = "live";

        private readonly ProcessRunner runner;

        public SshfsClient(OS os, ProcessRunner runner, string args) : base(os, runner, args)
        {
            this.runner = runner;
        }

        public override void Restart()
        {
            if (OS != OS.Linux) return; // sshfs is Linux-only by design for these tests

            var remote = $"{SshUser}@{SshfsServer.ServerIp}:{SshfsServer.ExportDir}";
            var mountOpts = "password_stdin,StrictHostKeyChecking=no,UserKnownHostsFile=/dev/null,reconnect,ServerAliveInterval=15";

            var script =
                $"fusermount -uz {MountPoint} 2>/dev/null || true; " +
                $"umount -l {MountPoint} 2>/dev/null || true; " +
                $"mkdir -p {MountPoint}; " +
                $"echo {SshPassword} | sshfs {remote} {MountPoint} -o {mountOpts}";

            runner.Run("bash", $"-c '{script}'");
        }
    }
}

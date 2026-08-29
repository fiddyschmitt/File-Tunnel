using System;
using System.IO;
using ft_tests.Runner;

namespace ft_tests.FileShares.Clients
{
    /// <summary>
    /// Mounts an sshfs export over sshfs on a Linux node (.80 side1 / .82 side2). The server it mounts is not fixed:
    /// a <see cref="SshfsMountSpec"/> names the host/port/user/export/auth, so the same client mounts the Linux (.81,
    /// password), Mac (.33, key) or Windows (.84, OpenSSH password) server - exactly like <see cref="AndroidSshfsClient"/>.
    /// The sshfs package is installed by provisioning (ft_test_env/Cloud/setup_debian.sh); Restart only does the
    /// runtime (re)mount, mirroring <see cref="NfsClient"/>:
    ///   1. tear down any stale/hung mount (fusermount -uz, then a lazy umount),
    ///   2. (re)mount user@host:export at a fixed local mount point.
    ///
    /// Auth: for a password server, sshfs's reliable non-interactive method is "-o password_stdin" fed the password on
    /// stdin (sshpass is unreliable with sshfs - it forks ssh without a tty). For a key server (the Mac), the private
    /// key is written onto the node (600 via umask) and passed as IdentityFile. The whole thing runs under sudo (ft
    /// also runs as root), so root owns the FUSE mount and reads/writes it without allow_other. StrictHostKeyChecking=no
    /// + UserKnownHostsFile=/dev/null avoid a first-connect host-key prompt that would otherwise hang the mount.
    /// </summary>
    public class SshfsClient : Client
    {
        // Server-agnostic mount point: a run mounts one server here, and Restart unmounts any stale mount first.
        public const string MountPoint = "/media/sshfs/export";

        // Where a key-auth private key is staged on the node (each Linux node writes its own copy).
        private const string NodeKeyPath = "/tmp/ft_sshfs_key";

        private readonly ProcessRunner runner;
        private readonly SshfsMountSpec spec;

        public SshfsClient(ProcessRunner runner, SshfsMountSpec spec, string args) : base(OS.Linux, runner, args)
        {
            this.runner = runner;
            this.spec = spec;
        }

        public override void Restart()
        {
            var remote = $"{spec.User}@{spec.Host}:{spec.ExportDir}";
            var common = "StrictHostKeyChecking=no,UserKnownHostsFile=/dev/null,reconnect,ServerAliveInterval=15";
            var portOpt = spec.Port == 22 ? "" : $" -p {spec.Port}";

            string mount;
            if (!string.IsNullOrEmpty(spec.Password))
            {
                mount = $"echo {spec.Password} | sshfs {remote} {MountPoint}{portOpt} -o password_stdin,{common}";
            }
            else
            {
                // Key auth: stage the private key on the node (600), then mount with IdentityFile.
                var b64 = Convert.ToBase64String(File.ReadAllBytes(spec.LocalKeyPath!));
                runner.Run("bash", $"-c 'umask 077; echo {b64} | base64 -d > {NodeKeyPath}'");
                mount = $"sshfs {remote} {MountPoint}{portOpt} -o IdentityFile={NodeKeyPath},IdentitiesOnly=yes,{common}";
            }

            var script =
                $"fusermount -uz {MountPoint} 2>/dev/null || true; " +
                $"umount -l {MountPoint} 2>/dev/null || true; " +
                $"mkdir -p {MountPoint}; " +
                mount;

            runner.Run("bash", $"-c '{script}'");
        }
    }
}

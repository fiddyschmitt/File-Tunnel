using ft_tests.Runner;

namespace ft_tests.FileShares.Clients
{
    /// <summary>How a client should mount an sshfs export: the SSH host (which varies - Linux .81 / Mac .33 /
    /// Windows .84 / another emulator's Termux sshd), its port, login user, remote export dir, and auth (a password
    /// for password_stdin, OR a local private-key path for key auth - the emulator client pushes it to the device).</summary>
    public record SshfsMountSpec(string Host, int Port, string User, string ExportDir, string? Password, string? LocalKeyPath);

    /// <summary>
    /// The Android emulator as an sshfs client (issue #45) - the Termux use case of `pkg install sshfs` then
    /// mounting a remote directory and running ft over it. <see cref="AndroidProcessRunner"/> stages the Termux
    /// sshfs toolchain onto the device and mounts the server named by the <see cref="SshfsMountSpec"/> at a
    /// per-instance mount point (each of the two emulators mounts independently, exactly like the Linux nodes).
    /// The mount is a real FUSE filesystem (statfs f_type == 0x65735546), so bionic ft auto-enables IsolatedIo
    /// over it just as for Linux sshfs - no ft change was needed for Android.
    /// </summary>
    public class AndroidSshfsClient : Client
    {
        // One mount per emulator ft instance so the two clients never share a mount point.
        public static string MountPoint(int instance) => $"/data/local/tmp/sshfs_mnt_{instance}";

        private readonly AndroidProcessRunner runner;
        private readonly int instance;
        private readonly SshfsMountSpec spec;

        public AndroidSshfsClient(AndroidProcessRunner runner, int instance, SshfsMountSpec spec, string args)
            : base(OS.Android, runner, args)
        {
            this.runner = runner;
            this.instance = instance;
            this.spec = spec;
        }

        public override void Restart()
        {
            runner.EnsureSshfsToolchain();
            string? deviceKey = null;
            if (string.IsNullOrEmpty(spec.Password) && !string.IsNullOrEmpty(spec.LocalKeyPath))
            {
                deviceKey = $"/data/local/tmp/sshfs_key_{instance}";
                runner.PushFile(spec.LocalKeyPath, deviceKey);
            }
            runner.MountSshfs(spec.User, spec.Host, spec.ExportDir, MountPoint(instance),
                              port: spec.Port, password: spec.Password, deviceKeyPath: deviceKey);
        }
    }
}

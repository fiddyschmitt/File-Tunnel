using ft_tests.Runner;

namespace ft_tests.FileShares.Servers
{
    /// <summary>
    /// The "direct" Android-to-Android sshfs server (issue #45): emulator 1 runs Termux sshd and hosts a local
    /// export dir. The other emulator (client2/side2) sshfs-mounts it, while emu1 itself (side1) reads/writes that
    /// export dir LOCALLY - "one Android SSHs into another Android, who writes to their local fs". Key auth: the lab
    /// keypair's public key is authorized in emu1's sshd, the private key is pushed to the client emulator.
    /// Reachable because emu1 is the BRIDGED emulator (real LAN IP); emu2 (NAT) dials it outbound.
    /// </summary>
    public class AndroidSshfsServer : Server
    {
        public const string ExportDir = "/data/local/tmp/sshfs_export";
        public const int SshdPort = 8022;
        public const string SshUser = "root";

        private readonly AndroidProcessRunner runner;
        private readonly string authorizedKeyText;

        public AndroidSshfsServer(AndroidProcessRunner runner, string authorizedKeyText)
            : base(OS.Android, FileShareType.Sshfs)
        {
            this.runner = runner;
            this.authorizedKeyText = authorizedKeyText;
        }

        public override void Restart()
        {
            runner.EnsureSshfsToolchain();
            runner.StartSshdServer(authorizedKeyText, ExportDir, SshdPort);
        }

        /// <summary>emu1's bridged LAN IP, where the client emulator dials sshd. Null if the bridge failed.</summary>
        public string? Host => runner.LanIp();
    }
}

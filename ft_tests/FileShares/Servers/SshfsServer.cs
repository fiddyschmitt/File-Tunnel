using ft_tests.Runner;

namespace ft_tests.FileShares.Servers
{
    /// <summary>
    /// "Server" for an sshfs share. sshfs simply tunnels file operations over an existing SSH
    /// server, so there is no dedicated daemon to manage here: sshd is already running (the test
    /// harness itself connects over it). Restart only ensures the exported directory exists and is
    /// world-writable so the SSH login user can read/write the files the clients transfer.
    ///
    /// Deliberately does NOT restart sshd — that would drop the harness's own SSH session to this
    /// node. The export lives on disk (not tmpfs) so it survives reboots; the 5 MB test payload
    /// makes the backing-store speed irrelevant next to the SSH transport.
    /// </summary>
    public class SshfsServer : Server
    {
        public const string ServerIp = "192.168.0.81";
        public const string ExportDir = "/srv/sshfs";

        private readonly ProcessRunner processRunner;

        public SshfsServer(ProcessRunner processRunner) : base(OS.Linux, FileShareType.Sshfs)
        {
            this.processRunner = processRunner;
        }

        public override void Restart()
        {
            processRunner.Run("bash", $"-c 'mkdir -p {ExportDir}; chmod 777 {ExportDir}'");
        }
    }
}

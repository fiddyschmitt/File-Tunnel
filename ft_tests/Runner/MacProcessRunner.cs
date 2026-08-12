using Renci.SshNet;
using System.Diagnostics;

namespace ft_tests.Runner
{
    // A macOS node. Modeled on LinuxProcessRunner, with two deliberate differences:
    //  - SSH KEY auth, not a password. The Mac is set up for public-key login; SSH.NET loads the
    //    private key (ed25519) and authenticates with it.
    //  - NO sudo to run ft (userspace) or to mount client shares (mount_smbfs as the user). The
    //    server-side SMB setup is the exception - it uses passwordless sudo granted on the Mac.
    //
    // There is only one physical Mac, so a combo with Mac on BOTH tunnel sides puts two ft processes on
    // it at once. Each runner instance therefore deploys ft under a UNIQUE name (ft-1, ft-2, ...) and
    // Stop() kills only that one by its full path - a global "pkill ft" would take out the other side.
    public class MacProcessRunner : ProcessRunner
    {
        private readonly SshClient sshClient;
        private readonly ConnectionInfo connectionInfo;
        private readonly string remoteExecutablePath;
        private readonly string outputFilename;

        public MacProcessRunner(string host, string username, string privateKeyPath, string localExecutablePath, string outputFilename, int instance = 1, int port = 22) : base(host)
        {
            var keyFile = new PrivateKeyFile(privateKeyPath);
            connectionInfo = new ConnectionInfo(host, port, username, new PrivateKeyAuthenticationMethod(username, keyFile));

            var remoteFolder = "/tmp/ft/";
            // Unique per instance so two ft processes can coexist on the one Mac; Stop() kills only this path.
            remoteExecutablePath = $"{remoteFolder}{Path.GetFileName(localExecutablePath)}-{instance}";

            sshClient = new SshClient(connectionInfo);
            sshClient.Connect();

            sshClient.CreateCommand($"mkdir -p \"{remoteFolder}\"").Execute();
            // One-time cleanup of strays, incl. the old single-name "ft" (safe: constructors run at
            // ClassInit, before any test launches ft).
            sshClient.CreateCommand("pkill -x ft || true").Execute();
            Stop();

            var scpClient = new ScpClient(connectionInfo);
            scpClient.Connect();
            scpClient.Upload(new FileInfo(localExecutablePath), remoteExecutablePath);

            sshClient.CreateCommand($"chmod +x \"{remoteExecutablePath}\"").Execute();
            this.outputFilename = outputFilename;
        }

        public override void Run(string args)
        {
            Stop();

            // Detach with nohup + &, redirecting to the output file so the SSH channel closes and
            // Execute() returns. No sudo (see class note).
            var command = $"bash -c 'nohup \"{remoteExecutablePath}\" {args} >> \"{outputFilename}\" 2>&1 &'";

            Debug.WriteLine(command);
            sshClient.CreateCommand(command).Execute();
        }

        public override string GetFullCommand(string args)
        {
            return $"\"{remoteExecutablePath}\" {args}";
        }

        public override TimeSpan? Stop()
        {
            // Kill only THIS instance's ft (matched by its unique full path), never a global pkill - the
            // other tunnel side may be a second ft on this same Mac.
            sshClient.CreateCommand($"pkill -f \"{remoteExecutablePath}\" || true").Execute();
            return null;
        }

        public override void DeleteFile(string path)
        {
            var deleteCmd = @$"while [ -e ""{path}"" ]; do rm -f ""{path}""; sleep 1; done";
            Debug.WriteLine(deleteCmd);
            sshClient.CreateCommand(deleteCmd).Execute();
        }

        public override void Run(string cmd, string args)
        {
            var command = $"\"{cmd}\" {args}";
            Debug.WriteLine(command);
            sshClient.CreateCommand(command).Execute();
        }

        public override (int ExitCode, string Output) RunCommand(string command)
        {
            Debug.WriteLine(command);
            using var sshCommand = sshClient.CreateCommand(command);
            var stdout = sshCommand.Execute();
            return (sshCommand.ExitStatus ?? -1, stdout + sshCommand.Error);
        }
    }
}

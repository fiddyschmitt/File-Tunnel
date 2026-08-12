using Renci.SshNet;
using System.Diagnostics;

namespace ft_tests.Runner
{
    // A macOS node. Modeled on LinuxProcessRunner, with two deliberate differences:
    //  - SSH KEY auth, not a password. The Mac is set up for public-key login; SSH.NET loads the
    //    private key (ed25519) and authenticates with it.
    //  - NO sudo. sudo needs a password on the Mac, and it isn't needed anyway: ft runs in userspace,
    //    and the SMB shares are mounted as the user (mount_smbfs, no root).
    public class MacProcessRunner : ProcessRunner
    {
        private readonly SshClient sshClient;
        private readonly ConnectionInfo connectionInfo;
        private readonly string remoteExecutablePath;
        private readonly string outputFilename;

        public MacProcessRunner(string host, string username, string privateKeyPath, string localExecutablePath, string outputFilename, int port = 22) : base(host)
        {
            var keyFile = new PrivateKeyFile(privateKeyPath);
            connectionInfo = new ConnectionInfo(host, port, username, new PrivateKeyAuthenticationMethod(username, keyFile));

            var remoteFolder = "/tmp/ft/";
            remoteExecutablePath = remoteFolder + Path.GetFileName(localExecutablePath);

            sshClient = new SshClient(connectionInfo);
            sshClient.Connect();

            sshClient.CreateCommand($"mkdir -p \"{remoteFolder}\"").Execute();

            Stop();

            var scpClient = new ScpClient(connectionInfo);
            scpClient.Connect();

            Stop();
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
            var processName = Path.GetFileName(remoteExecutablePath);
            sshClient.CreateCommand($"pkill -x \"{processName}\" || true").Execute();
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

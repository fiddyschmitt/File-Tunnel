using Renci.SshNet;

namespace ft_tests.Runner
{
    public class LinuxProcessRunner : ProcessRunner
    {
        private readonly SshClient sshClient;
        private readonly string host;
        private readonly string remoteExecutablePath;

        public LinuxProcessRunner(string host, string username, string password, string localExecutablePath, string remoteExecutablePath = null)
        {
            var remoteFolder = "/tmp/ft/";
            this.remoteExecutablePath = remoteFolder + Path.GetFileName(localExecutablePath);

            sshClient = new SshClient(host, username, password);
            sshClient.Connect();

            sshClient.RunCommand($"mkdir -p \"{remoteFolder}\"");

            Stop();

            var scpClient = new ScpClient(host, username, password);
            scpClient.Connect();
            scpClient.Upload(new FileInfo(localExecutablePath), this.remoteExecutablePath);

            sshClient.RunCommand($"chmod +x \"{this.remoteExecutablePath}\"");

            this.host = host;
        }

        public override void Run(string args)
        {
            Stop();

            // Run the process in background (&) to detach
            var command = $"nohup \"{remoteExecutablePath}\" {args} > /dev/null 2>&1 &";
            sshClient.RunCommand(command);
        }

        public override void Stop()
        {
            var processName = Path.GetFileName(remoteExecutablePath);
            // pkill by name to stop the process
            sshClient.RunCommand($"pkill -f \"{processName}\" || true");
        }
    }
}

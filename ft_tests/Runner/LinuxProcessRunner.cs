using Renci.SshNet;
using System.Diagnostics;

namespace ft_tests.Runner
{
    public class LinuxProcessRunner : ProcessRunner
    {
        private readonly SshClient sshClient;
        private readonly string remoteExecutablePath;

        public LinuxProcessRunner(string host, string username, string password, string localExecutablePath, string remoteExecutablePath = null) : base(host)
        {
            var remoteFolder = "/tmp/ft/";
            this.remoteExecutablePath = remoteFolder + Path.GetFileName(localExecutablePath);

            sshClient = new SshClient(host, username, password);
            sshClient.Connect();

            sshClient.CreateCommand($"mkdir -p \"{remoteFolder}\"").Execute();

            Stop();

            var scpClient = new ScpClient(host, username, password);
            scpClient.Connect();
            scpClient.Upload(new FileInfo(localExecutablePath), this.remoteExecutablePath);

            sshClient.CreateCommand($"chmod +x \"{this.remoteExecutablePath}\"").Execute();
        }

        public override void Run(string args)
        {
            Stop();

            // Run the process in background (&) to detach
            var command = $"nohup sudo \"{remoteExecutablePath}\" {args} > /dev/null 2>&1 &";
            Debug.WriteLine($"{command}");
            sshClient.CreateCommand(command).Execute();
        }

        public override string GetFullCommand(string args)
        {
            var command = $"sudo \"{remoteExecutablePath}\" {args}";
            return command;
        }

        public override void Stop()
        {
            var processName = Path.GetFileName(remoteExecutablePath);
            // pkill by name to stop the process
            sshClient.CreateCommand($"sudo pkill -f \"{processName}\" || true").Execute();
        }

        public override void DeleteFile(string path)
        {
            var deleteCmd = @$"while [ -e ""{path}"" ]; do sudo rm -f ""{path}""; sleep 1; done";
            //var deleteCmd = @$"for i in {{1..10}}; do rm -f ""{path}""; sleep 1; done";
            Debug.WriteLine(deleteCmd);
            sshClient.CreateCommand(deleteCmd).Execute();
        }

        public override void Run(string cmd, string args)
        {
            var command = $"sudo \"{cmd}\" {args}";
            Debug.WriteLine($"{command}");
            sshClient.CreateCommand(command).Execute();
        }
    }
}

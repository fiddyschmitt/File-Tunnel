using Renci.SshNet;
using System.Diagnostics;

namespace ft_tests.Runner
{
    public class LinuxProcessRunner : ProcessRunner
    {
        private readonly SshClient sshClient;
        private readonly string remoteExecutablePath;
        private readonly string outputFilename;

        public LinuxProcessRunner(string host, string username, string password, string localExecutablePath, string outputFilename) : base(host)
        {
            var remoteFolder = "/tmp/ft/";
            this.remoteExecutablePath = remoteFolder + Path.GetFileName(localExecutablePath);

            sshClient = new SshClient(host, username, password);
            sshClient.Connect();

            sshClient.CreateCommand($"mkdir -p \"{remoteFolder}\"").Execute();

            Stop();

            var scpClient = new ScpClient(host, username, password);
            scpClient.Connect();

            Stop();
            scpClient.Upload(new FileInfo(localExecutablePath), remoteExecutablePath);

            sshClient.CreateCommand($"chmod +x \"{this.remoteExecutablePath}\"").Execute();
            this.outputFilename = outputFilename;
        }

        public override void Run(string args)
        {
            Stop();

            // Run the process in background (&) to detach
            var command = $"sudo bash -c 'nohup \"{remoteExecutablePath}\" {args} >> \"{outputFilename}\" 2>&1 &'";

            Debug.WriteLine($"{command}");
            sshClient.CreateCommand(command).Execute();
        }

        public override string GetFullCommand(string args)
        {
            var command = $"sudo \"{remoteExecutablePath}\" {args}";
            return command;
        }

        public override TimeSpan? Stop()
        {
            var processName = Path.GetFileName(remoteExecutablePath);
            // pkill by name to stop the process
            sshClient.CreateCommand($"sudo pkill -x \"{processName}\" || true").Execute();

            return null;
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

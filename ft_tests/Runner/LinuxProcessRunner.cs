using Renci.SshNet;
using System.Diagnostics;

namespace ft_tests.Runner
{
    public class LinuxProcessRunner : ProcessRunner
    {
        private readonly SshClient sshClient;
        private readonly string remoteExecutablePath;
        private readonly string outputFilename;

        // port defaults to 22; the nested QEMU guest is reached via the host's SSH port-forward (.82:2222).
        public LinuxProcessRunner(string host, string username, string password, string localExecutablePath, string outputFilename, int port = 22) : base(host)
        {
            var remoteFolder = "/tmp/ft/";
            this.remoteExecutablePath = remoteFolder + Path.GetFileName(localExecutablePath);

            sshClient = new SshClient(host, port, username, password);
            sshClient.Connect();

            sshClient.ExecuteBounded($"mkdir -p \"{remoteFolder}\"");

            Stop();

            var scpClient = new ScpClient(host, port, username, password);
            scpClient.Connect();

            Stop();
            scpClient.Upload(new FileInfo(localExecutablePath), remoteExecutablePath);

            sshClient.ExecuteBounded($"chmod +x \"{this.remoteExecutablePath}\"");
            this.outputFilename = outputFilename;
        }

        public override void Run(string args)
        {
            Stop();

            // Run the process in background (&) to detach
            var command = $"sudo bash -c 'nohup \"{remoteExecutablePath}\" {args} >> \"{outputFilename}\" 2>&1 &'";

            Debug.WriteLine($"{command}");
            sshClient.ExecuteBounded(command);
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
            sshClient.ExecuteBounded($"sudo pkill -x \"{processName}\" || true");

            return null;
        }

        public override void DeleteFile(string path)
        {
            var deleteCmd = @$"while [ -e ""{path}"" ]; do sudo rm -f ""{path}""; sleep 1; done";
            //var deleteCmd = @$"for i in {{1..10}}; do rm -f ""{path}""; sleep 1; done";
            Debug.WriteLine(deleteCmd);
            sshClient.ExecuteBounded(deleteCmd);
        }

        public override void Run(string cmd, string args)
        {
            var command = $"sudo \"{cmd}\" {args}";
            Debug.WriteLine($"{command}");
            sshClient.ExecuteBounded(command);
        }

        public override (int ExitCode, string Output) RunCommand(string command)
        {
            Debug.WriteLine(command);
            var (output, status, completed) = sshClient.ExecuteHardBounded(command);
            return completed ? (status, output) : (-1, "[ssh command timed out]");
        }
    }
}

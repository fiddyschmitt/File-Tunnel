﻿using Renci.SshNet;
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

            sshClient.RunCommand($"mkdir -p \"{remoteFolder}\"");

            Stop();

            var scpClient = new ScpClient(host, username, password);
            scpClient.Connect();
            scpClient.Upload(new FileInfo(localExecutablePath), this.remoteExecutablePath);

            sshClient.RunCommand($"chmod +x \"{this.remoteExecutablePath}\"");
        }

        public override void Run(string args)
        {
            Stop();

            // Run the process in background (&) to detach
            var command = $"nohup sudo \"{remoteExecutablePath}\" {args} > /dev/null 2>&1 &";
            Debug.WriteLine($"{command}");
            sshClient.RunCommand(command);
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
            sshClient.RunCommand($"sudo pkill -f \"{processName}\" || true");
        }
    }
}

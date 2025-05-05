using ft;
using Renci.SshNet;
using System;
using System.Diagnostics;

namespace ft_tests.Runner
{
    public class RemoteWindowsProcessRunner : ProcessRunner
    {
        SshClient sshClient;
        private readonly string host;
        private readonly string remoteExecutablePath;


        public RemoteWindowsProcessRunner(string host, string username, string password, string localExecutablePath)
        {
            var remoteFolder = "/C:/Temp/ft/";
            remoteExecutablePath = remoteFolder + Path.GetFileName(localExecutablePath);

            sshClient = new SshClient(host, username, password);
            sshClient.Connect();
            sshClient.RunCommand(@$"mkdir ""{remoteFolder}""");



            var processName = Path.GetFileName(remoteExecutablePath);
            Kill(processName);

            var scpClient = new ScpClient(host, username, password);
            scpClient.Connect();
            scpClient.Upload(new FileInfo(localExecutablePath), remoteExecutablePath);

            this.host = host;

            remoteExecutablePath = Path.Combine(@"C:\Temp\ft", Path.GetFileName(localExecutablePath));
        }

        public override void Run(string args)
        {
            var processName = Path.GetFileName(remoteExecutablePath);
            Kill(processName);

            var rr = @"C:\Users\Smith\Desktop\dev\cs\RunRemote\runremote\bin\Debug\net8.0\runremote.exe";

            var rrArgs = $"{host}:8888 \"{remoteExecutablePath}\" {args}";

            Process.Start(rr, rrArgs);
        }

        public override void Kill(string process)
        {
            sshClient.RunCommand(@$"taskkill /IM {process} /F");
        }
    }
}

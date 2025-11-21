using ft;
using Renci.SshNet;
using System;
using System.Diagnostics;

namespace ft_tests.Runner
{
    public class RemoteWindowsProcessRunner : ProcessRunner
    {
        readonly SshClient sshClient;
        private readonly string host;
        private readonly string? remoteExecutablePath;


        public RemoteWindowsProcessRunner(string host, string username, string password, string? localExecutablePath = null) : base(host)
        {
            if (localExecutablePath != null)
            {
                var remoteFolder = "/C:/Temp/ft/";
                remoteExecutablePath = remoteFolder + Path.GetFileName(localExecutablePath);

                sshClient = new SshClient(host, username, password);
                sshClient.Connect();
                sshClient.CreateCommand(@$"mkdir ""{remoteFolder}""").Execute();


                Stop();

                var scpClient = new ScpClient(host, username, password);
                scpClient.Connect();
                scpClient.Upload(new FileInfo(localExecutablePath), remoteExecutablePath);

                this.host = host;

                remoteExecutablePath = Path.Combine(@"C:\Temp\ft", Path.GetFileName(localExecutablePath));
            }
        }

        public override void Run(string args)
        {
            var rr = @"C:\Users\Smith\Desktop\dev\cs\RunRemote\runremote\bin\Debug\net8.0\runremote.exe";

            var rrArgs = $"{host}:8888 \"{remoteExecutablePath}\" {args}";

            Debug.WriteLine($"\"{remoteExecutablePath}\" {args}");

            Process.Start(rr, rrArgs);
        }

        public override string GetFullCommand(string args)
        {
            var result = $"\"{remoteExecutablePath}\" {args}";
            return result;
        }

        public override TimeSpan? Stop()
        {
            var processName = Path.GetFileName(remoteExecutablePath);
            sshClient.CreateCommand(@$"taskkill /IM {processName} /F").Execute();

            return null;
        }

        public override void DeleteFile(string path)
        {
            var cmd = @$"@echo off & :loop & if exist ""{path}"" del /f /q ""{path}"" & if exist ""{path}"" timeout /t 1 >nul & goto loop";
            Debug.WriteLine(cmd);
            sshClient.CreateCommand(cmd).Execute();
        }

        public override void Run(string cmd, string args)
        {
            var rr = @"C:\Users\Smith\Desktop\dev\cs\RunRemote\runremote\bin\Debug\net8.0\runremote.exe";

            var rrArgs = $"{host}:8888 \"{cmd}\" {args}";

            Debug.WriteLine($"\"{cmd}\" {args}");

            Process.Start(rr, rrArgs);

            Thread.Sleep(5000);
        }
    }
}

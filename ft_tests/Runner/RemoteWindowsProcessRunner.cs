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

        // The runremote client that asks the node's runremote server (UDP 8888) to launch ft in its interactive
        // session. Wait for its ack (bounded) so a dropped datagram is retried and a dead runremote surfaces here.
        const string RunRemoteClient = @"C:\Users\Smith\Desktop\dev\cs\RunRemote\runremote\bin\Debug\net8.0\runremote.exe";

        private void InvokeRunRemote(string target, string trailing)
        {
            var rrArgs = $"{host}:8888 \"{target}\" {trailing}";
            Debug.WriteLine($"\"{target}\" {trailing}");
            var psi = new ProcessStartInfo(RunRemoteClient, rrArgs)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) { Debug.WriteLine($"runremote: could not start client for {host}"); return; }
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } Debug.WriteLine($"runremote: client to {host} timed out"); return; }
            var outp = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            if (p.ExitCode != 0)
                Debug.WriteLine($"WARNING runremote on {host} did not launch (exit {p.ExitCode}): {outp}");
            else
                Debug.WriteLine($"runremote {host}: {outp}");
        }

        // True if the node's runremote server answers a PING - a fast liveness probe before relying on it.
        public bool RunRemoteAlive()
        {
            try
            {
                var psi = new ProcessStartInfo(RunRemoteClient, $"{host}:8888 PING")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi);
                if (p == null || !p.WaitForExit(6000)) { try { p?.Kill(); } catch { } return false; }
                return p.ExitCode == 0 && p.StandardOutput.ReadToEnd().Contains("PONG");
            }
            catch { return false; }
        }

        public override void Run(string args)
        {
            InvokeRunRemote(remoteExecutablePath ?? "", args);
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
            InvokeRunRemote(cmd, args);
            Thread.Sleep(5000);
        }

        // A console command (e.g. curl) runs fine over the existing SSH channel and its output is captured -
        // unlike ft's launch, which needs the interactive-session runremote path. Loopback (127.0.0.1) is
        // not session-isolated, so curl here can reach ft's SOCKS port regardless of which session ft runs in.
        public override (int ExitCode, string Output) RunCommand(string command)
        {
            if (sshClient == null) throw new InvalidOperationException("RunCommand requires the SSH client (constructed with a non-null executable path).");
            Debug.WriteLine(command);
            using var sshCommand = sshClient.CreateCommand(command);
            var stdout = sshCommand.Execute();
            return (sshCommand.ExitStatus ?? -1, stdout + sshCommand.Error);
        }
    }
}
